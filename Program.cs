using System.Text;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

public class NoogleArgs
{
    public string Path { get; set; }
    public string Type { get; set; }
    public string Method { get; set; }
    public string Lib { get; set; }
    public bool PublicOnly { get; set; } = true;
}

public class Program
{
    public static void Main(string[] args)
    {
        if (!ParseArgs(args, out var noogleArgs))
        {
            return;
        }
        var path = @"C:\Projects\noodletmp\bin\Debug\net8.0\noodletmp.dll";
        /*var folder = @"c:\Projects\omnisharp\omnisharp-roslyn\bin\Debug\OmniSharp.Host\net6.0\";*/

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
        using var peFile = new PEFile(path, fs);
        var resolver = new UniversalAssemblyResolver(path, false, peFile.Metadata.DetectTargetFrameworkId());
        var typeSystem = new DecompilerTypeSystem(peFile, resolver);
        foreach (var type in typeSystem.MainModule.TypeDefinitions)
        {
            foreach (var method in type.Methods)
            {
                Print(type, method);
            }
        }
    }

    public static bool ParseArgs(string[] args, out NoogleArgs res)
    {
        res = new NoogleArgs();
        var ind = 0;
        while (ind < args.Length)
        {
            var arg = args[ind];
            if (arg == "-p" && ind < args.Length - 1)
            {
                ind++;
                res.Path = args[ind];
                ind++;
            } 
            else if (arg == "-l" && ind < args.Length - 1)
            {
                ind++;
                res.Lib = args[ind];
                ind++;
            } 
            else if (arg == "-t" && ind < args.Length - 1)
            {
                ind++;
                res.Type = args[ind];
                ind++;
            } 
            else if (arg == "-m" && ind < args.Length - 1)
            {
                ind++;
                res.Method = args[ind];
                ind++;
            }
            else if (arg == "-a")
            {
                ind++;
                res.PublicOnly = false;
            } 
            else if (arg == "-?")
            {
                PrintUsage(Console.Out);
                return false;
            } 
            else 
            {
                PrintInvalidArgs();
                return false;
            }
        }
        return true;
    }

    public static void PrintInvalidArgs()
    {
        var tw = Console.Error;
        tw.WriteLine("Invalid argument list");
        tw.WriteLine();
        PrintUsage(tw);
    }

    public static void PrintUsage(TextWriter tw)
    {
        tw.WriteLine("noogle [args]:");
        tw.WriteLine("    -p <path1>:    path");
        tw.WriteLine("    -l <library1>: library");
        tw.WriteLine("    -t <type1>:    type name");
        tw.WriteLine("    -m <method1>:  method name");
        tw.WriteLine("    -a:            all accessibility (public, private, etc.)");
        tw.WriteLine("    -?:            show this message");
    }

    private static void FindByType(string typeName, 
        bool any)
    {

    }

    private static void Print(
        IType type,
        IMethod method
    )
    {
        var returnType = method.ReturnType;
        var parameters = method.Parameters;
        var sb = new StringBuilder();
        sb.Append(type.FullName);
        sb.Append(" ");
        sb.Append(method.IsStatic ? "#" : "&");
        sb.Append(" ");
        PrintType(sb, returnType);
        sb.Append(" ");
        sb.Append(method.Name);
        sb.Append("(");
        foreach (var paramInfo in parameters)
        {
            PrintType(sb, paramInfo.Type);
            sb.Append(" ");
            sb.Append(paramInfo.Name);
            if (paramInfo.IsOptional)
                sb.Append(" = null");
            sb.Append(", ");
        }
        if (parameters.Any())
        {
            sb.Remove(sb.Length - 2, 2);
        }
        sb.Append(")");
        Console.WriteLine(sb.ToString());
    }

    private static void PrintType(StringBuilder sb, IType type)
    {
        sb.Append(Type(type.Name));
        if (!type.TypeArguments.Any())
            return;
        sb.Append("<");
        foreach (var typeParam in type.TypeArguments)
        {
            PrintType(sb, typeParam);
            sb.Append(", ");
        }
        sb.Remove(sb.Length - 2, 2);
        sb.Append(">");
    }

    private static string Type(string typeName) =>
        typeName switch 
        {
            "Int32" => "int",
            "String" => "string",
            "Void" => "void",
            _ => typeName
        };
}
