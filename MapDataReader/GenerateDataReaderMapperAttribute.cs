using System;

namespace MapDataReader;

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