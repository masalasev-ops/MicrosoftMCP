using System.Reflection;
var baseDir = System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".nuget/packages");
// Need to resolve Microsoft.Extensions.AI assembly
var aiAsm = Assembly.LoadFrom(System.IO.Path.Combine(baseDir, "microsoft.extensions.ai/10.7.0/lib/net10.0/Microsoft.Extensions.AI.dll"));
var t = aiAsm.GetExportedTypes().First(t => t.Name == "FunctionInvokingChatClient");
Console.WriteLine($"=== {t.FullName} ===");
foreach (var p in t.GetProperties()) Console.WriteLine($"  {p.PropertyType.Name} {p.Name}");
Console.WriteLine("\n=== Constructors ===");
foreach (var c in t.GetConstructors()) Console.WriteLine($"  ctor({string.Join(", ", c.GetParameters().Select(p => $"{p.ParameterType.Name} {p.Name}"))})");
