# MapDataReader
Super fast mapping DataReader to a strongly typed object. High performance, lighweight (12Kb dll), uses AOT source generation and no reflection, mapping code is generated at compile time.

[![.NET](https://github.com/jitbit/MapDataReader/actions/workflows/dotnet.yml/badge.svg)](https://github.com/jitbit/MapDataReader/actions/workflows/dotnet.yml)
[![Nuget](https://img.shields.io/nuget/v/MapDataReader)](https://www.nuget.org/packages/MapDataReader/)
![Net stanrdard 2.0](https://img.shields.io/badge/netstandard-2.0-brightgreen)

## Benchmarks

20X faster than using reflection, even with caching. Benchmark for a tiny class with 5 string properties:

| Method         |      Mean |     Error |   StdDev |   Gen0 | Allocated |
|--------------- |----------:|----------:|---------:|-------:|----------:|
|  Reflection    | 951.16 ns | 15.107 ns | 0.828 ns | 0.1459 |     920 B |
|  MapDataReader |  44.15 ns |  2.840 ns | 0.156 ns | 0.0089 |      56 B |

## Install via [Nuget](https://www.nuget.org/packages/MapDataReader/)

```
Install-Package MapDataReader
```

## Usage with `IDataReader`

```csharp
using MapDataReader;

[GenerateDataReaderMapper] // <-- mark your class with this attribute
public class MyClass
{
	public int ID { get; set; }
	public string Name { get; set; }
	public int Size { get; set; }
	public bool Enabled { get; set; }
}

var dataReader = new SqlCommand("SELECT * FROM MyTable", connection).ExecuteReader();

List<MyClass> results = dataReader.ToMyClass(); // "ToMyClass" method is generated at compile time
```

Some notes for the above

* The `ToMyClass()` method above - is an `IDataReader` extension method generated at compile time. You can even "go to definition" in Visual Studio and examine its code.
* The naming convention is `ToCLASSNAME()` we can't use generics here, since `<T>` is not part of method signatures in C# (considered in later versions of C#). If you find a prettier way - please contribute!
* Maps properies with public setters only.
* The datareader is being closed after mapping, so don't reuse it.
* Supports `enum` properties based on `int` and other implicit casting (sometimes a DataReader may decide to return `byte` for small integer database value, and it maps to `int` perfectly via some unboxing magic)
* Properly maps `DBNull` to `null`.
* Complex-type properties may not work.

### Access Modifier: `public` or `internal`

You can now specify the access modifer to be used with the mapping methods. By default, the methods will be `public` for backwards compatability.

For example, to prevent exposure outside your assembly you'd set it to `internal`. This would hide the mapping methods outside your model project:

``` csharp
[GenerateDataReaderMapper("internal")]
public class MyClass
{
    public int ID { get; set; }
...
```

## Bonus API: `SetPropertyByName`

This package also adds a super fast `SetPropertyByName` extension method generated at compile time for your class.

Usage:

```csharp
var x = new MyClass();
x.SetPropertyByName("Size", 42); //20X faster than using reflection
```

|                  Method |      Mean |     Error |    StdDev | Allocated |
|------------------------ |----------:|----------:|----------:|----------:|
|       SetPropReflection | 98.294 ns | 5.7443 ns | 0.3149 ns |         - |
| SetPropReflectionCached | 71.137 ns | 1.9736 ns | 0.1082 ns |         - |
|    SetPropMapDataReader |  4.711 ns | 0.4640 ns | 0.0254 ns |         - |

---

## Tip: What's actually generated?

It can be difficult to see the output of the generated files. You might want to check them to see if something has gone wrong (if so please raise an issue). You might also want to include the generated classes in your source control to keep track of any unintended changes.

If you update your project file with the following properties, it will tell the source generator to save the files to disk in the specified location.

1. `<EmitCompilerGeneratedFiles>`: Set this to `true` to save the generated files to disk.
2. `<GeneratedFolder>`: Set this to change the folder (relative to your project root folder) where the generated files are saved.
3. *Optional*: `<CompilerGeneratedFilesOutputPath>`: Set this to further split up the generated files by the target framework (net481, netstandard2.0, net8.0, etc.). If you're only targeting one framework during compilation, you don't need this property.
4. `<Compile Remove="$(GeneratedFolder)/**/*.cs" />`: This is needed to exclude the generated files from compilation as the in-memory source generator is used during compilation.

```xml
<PropertyGroup>
    <EmitCompilerGeneratedFiles>true</EmitCompilerGeneratedFiles>
    <GeneratedFolder>Generated</GeneratedFolder>
    <CompilerGeneratedFilesOutputPath>$(GeneratedFolder)\$(TargetFramework)</CompilerGeneratedFilesOutputPath>
</PropertyGroup>

<ItemGroup>
    <Compile Remove="$(GeneratedFolder)/**/*.cs" />
</ItemGroup>
```

[More information on saving source generator output](https://andrewlock.net/creating-a-source-generator-part-6-saving-source-generator-output-in-source-control/)

---

## Tip: Using it with Dapper

If you're already using the awesome [Dapper ORM](https://github.com/DapperLib/Dapper) by Marc Gravel, Sam Saffron and Nick Craver, this is how you can use our library to speed up DataReader-to-object mapping in Dapper:

```csharp
// override Dapper extension method to use fast MapDataReader instead of Dapper's built-in reflection
public static List<T> Query<T>(this SqlConnection cn, string sql, object parameters = null)
{
	if (typeof(T) == typeof(MyClass)) //our own class that we marked with attribute?
		return cn.ExecuteReader(sql, parameters).ToMyClass() as List<T>; //use MapDataReader

	if (typeof(T) == typeof(AnotherClass)) //another class we have enabled?
		return cn.ExecuteReader(sql, parameters).ToAnotherClass() as List<T>; //again

	//fallback to Dapper by default
	return SqlMapper.Query<T>(cn, sql, parameters).AsList();
}
```
Why the C# compiler will choose your method over Dapper's?

When the C# compiler sees two extension methods with the same signature, it uses the one that's "closer" to your code. "Closiness" - is determined by multiple factors - same namespace, same assembly, derived class over base class, implementation over interface etc. Adding an override like this will silently switch your existing code from using Dapper/reflection to using our source generator (b/c it uses a more specific connection type and lives in your project's namescape), while still keeping the awesomeness of Dapper and you barely have to rewrite any of your code.

---

## P.S. But what's the point?

While reflection-based ORMs like Dapper are very fast after all the reflaction objects have been cached, they still do a lot of reflection-based heavy-lifting when you query the database *for the first time*. Which slows down application startup *significantly*. Which, in turn, can become a problem if you deploy the application multiple times a day.

Or - if you run your ASP.NET Core app on IIS - this causes 503 errors during IIS recycles, see https://github.com/dotnet/aspnetcore/issues/41340 and faster app startup helps a lot.

Also, reflection-caching causes memory pressure becasue of all the concurrent dictionaries used for caching.

And even with all the caching, a simple straightforward code like `obj.x = y` will always be faster then looking up a cached delegate in a thousands-long dictionary by a string key and invoking it via reflection.

Even if you don't care about the startup performance of your app, `MapDataReader` is still 5-7% faster than `Dapper` (note - we're not even using Dapper's command-cache store here, just the datareader parser, actual real world Dapper scenario will be even slower)

|          Method |          Mean |         Error |       StdDev |   Gen0 |   Gen1 | Allocated |
|---------------- |--------------:|--------------:|-------------:|-------:|-------:|----------:|
| DapperWithCache |     142.09 us |  8,013.663 ns |   439.256 ns | 9.0332 | 1.2207 |   57472 B |
|   MapDataReader |     133.22 us | 28,679.198 ns | 1,572.004 ns | 9.0332 | 1.2207 |   57624 B |

