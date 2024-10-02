﻿using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;

namespace MapDataReader
{
	[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
	public class GenerateDataReaderMapperAttribute : Attribute
	{
		public string AccessModifier { get; set; }

		public GenerateDataReaderMapperAttribute()
		{
			AccessModifier = "public";
		}

		public GenerateDataReaderMapperAttribute(string access = "public")
		{
			AccessModifier = access;
		}
	}

	[Generator]
	public class MapperGenerator : ISourceGenerator
	{
		public void Execute(GeneratorExecutionContext context)
		{
			var targetTypeTracker = context.SyntaxContextReceiver as TargetTypeTracker;

			foreach (var typeNode in targetTypeTracker.TypesNeedingGening)
			{
				var typeNodeSymbol = context.Compilation
					.GetSemanticModel(typeNode.SyntaxTree)
					.GetDeclaredSymbol(typeNode);

				var allProperties = typeNodeSymbol.GetAllSettableProperties();

				var src = $@"
					// <auto-generated/>
					#pragma warning disable 8019 //disable 'unnecessary using directive' warning
					using System;
					using System.Data;
					using System.Linq;
					using System.Collections.Generic; //to support List<T> etc

					namespace MapDataReader
					{{
						public static partial class MapperExtensions
						{{
							public static void SetPropertyByName(this {typeNodeSymbol.FullName()} target, string name, object value)
							{{
								SetPropertyByUpperName(target, name.ToUpperInvariant(), value);
							}}

							private static void SetPropertyByUpperName(this {typeNodeSymbol.FullName()} target, string name, object value)
							{{
								{"\r\n" + allProperties.Select(p =>
								{
									var pTypeName = p.Type.FullName();

									if (p.Type.IsReferenceType) //ref types - just cast to property type
									{
										return $@"	if (name == ""{p.Name.ToUpperInvariant()}"") {{ target.{p.Name} = value as {pTypeName}; return; }}";
									}
									else if (pTypeName.EndsWith("?") && !p.Type.IsNullableEnum()) //nullable type (unless nullable Enum)
									{
										var nonNullableTypeName = pTypeName.TrimEnd('?');

										//do not use "as" operator becasue "as" is slow for nullable types. Use "is" and a null-check
										return $@"	if (name == ""{p.Name.ToUpperInvariant()}"") {{ if(value==null) target.{p.Name}=null; else if(value is {nonNullableTypeName}) target.{p.Name}=({nonNullableTypeName})value; return; }}";
									}
									else if (p.Type.TypeKind == TypeKind.Enum || p.Type.IsNullableEnum()) //enum? pre-convert to underlying type then to int, you can't cast a boxed int to enum directly. Also to support assigning "smallint" database col to int32 (for example), which does not work at first (you can't cast a boxed "byte" to "int")
									{
										return $@"	if (value != null && name == ""{p.Name.ToUpperInvariant()}"") {{ target.{p.Name} = ({pTypeName})(value.GetType() == typeof(int) ? (int)value : (int)Convert.ChangeType(value, typeof(int))); return; }}"; //pre-convert enums to int first (after unboxing, see below)
									}
									else //primitive types. use Convert.ChangeType before casting. To support assigning "smallint" database col to int32 (for example), which does not work at first (you can't cast a boxed "byte" to "int")
									{
										return $@"	if (value != null && name == ""{p.Name.ToUpperInvariant()}"") {{ target.{p.Name} = value.GetType() == typeof({pTypeName}) ? ({pTypeName})value : ({pTypeName})Convert.ChangeType(value, typeof({pTypeName})); return; }}";
									}
								}).StringConcat("\r\n") } 


							}} //end method";

				if (typeNodeSymbol.InstanceConstructors.Any(c => !c.Parameters.Any())) //has a constructor without parameters?
				{
					src += $@"

							public static List<{typeNodeSymbol.FullName()}> To{typeNode.Identifier}(this IDataReader dr)
							{{
								var list = new List<{typeNodeSymbol.FullName()}>();
								
								if (dr.Read())
								{{
									string[] columnNames = new string[dr.FieldCount];
									
									for (int i = 0; i < columnNames.Length; i++)
										columnNames[i] = dr.GetName(i).ToUpperInvariant();

									do
									{{
										var result = new {typeNodeSymbol.FullName()}();
										for (int i = 0; i < columnNames.Length; i++)
										{{
											var value = dr[i];
											if (value is DBNull) value = null;
											SetPropertyByUpperName(result, columnNames[i], value);
										}}
										list.Add(result);
									}} while (dr.Read());
								}}
								dr.Close();
								return list;
							}}";
				}

				src += "\n}"; //end class
				src += "\n}"; //end namespace

				// Add the source code to the compilation
				context.AddSource($"{typeNodeSymbol.Name}DataReaderMapper.g.cs", src);
			}
		}

		public void Initialize(GeneratorInitializationContext context)
		{
			context.RegisterForSyntaxNotifications(() => new TargetTypeTracker());
		}
	}

	internal class TargetTypeTracker : ISyntaxContextReceiver
	{
		public IImmutableList<ClassDeclarationSyntax> TypesNeedingGening = ImmutableList.Create<ClassDeclarationSyntax>();

		public void OnVisitSyntaxNode(GeneratorSyntaxContext context)
		{
			if (context.Node is ClassDeclarationSyntax cdecl)
				if (cdecl.IsDecoratedWithAttribute("GenerateDataReaderMapper"))
					TypesNeedingGening = TypesNeedingGening.Add(cdecl);
		}
	}

	internal static class Helpers
	{
		internal static bool IsDecoratedWithAttribute(this TypeDeclarationSyntax cdecl, string attributeName) =>
			cdecl.AttributeLists
				.SelectMany(x => x.Attributes)
				.Any(x => x.Name.ToString().Contains(attributeName));


		internal static string FullName(this ITypeSymbol typeSymbol) => typeSymbol.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);

		internal static string StringConcat(this IEnumerable<string> source, string separator) => string.Join(separator, source);

		// returns all properties with public setters
		internal static IEnumerable<IPropertySymbol> GetAllSettableProperties(this ITypeSymbol typeSymbol)
		{
			var result = typeSymbol
				.GetMembers()
				.Where(s => s.Kind == SymbolKind.Property).Cast<IPropertySymbol>() //get all properties
				.Where(p => p.SetMethod?.DeclaredAccessibility == Accessibility.Public) //has a public setter?
				.ToList();

			//now get the base class
			var baseType = typeSymbol.BaseType;
			if (baseType != null)
				result.AddRange(baseType.GetAllSettableProperties()); //recursion

			return result;
		}

		//checks if type is a nullable num
		internal static bool IsNullableEnum(this ITypeSymbol symbol)
		{
			//tries to get underlying non-nullable type from nullable type
			//and then check if it's Enum
			if (symbol.NullableAnnotation == NullableAnnotation.Annotated
				&& symbol is INamedTypeSymbol namedType
				&& namedType.IsValueType
				&& namedType.IsGenericType
				&& namedType.ConstructedFrom?.ToDisplayString() == "System.Nullable<T>"
			)
				return namedType.TypeArguments[0].TypeKind == TypeKind.Enum;

			return false;
		}
	}
}