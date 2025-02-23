﻿using System.Text;
using System.Reflection.PortableExecutable;
using System.Reflection.Metadata;
using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace noogle;

public class NoogleArgs
{
    public string Path { get; set; }
    public string Type { get; set; }
    public string Member { get; set; }
    public string Lib { get; set; }
    public bool CtorsOnly { get; set; }
    public bool PublicOnly { get; set; } = true;
}

public class TypeInfo
{
    public List<IMethod> Ctors { get; set; } = new ();
    public List<IMethod> Methods { get; set; } = new ();
    public List<IProperty> Props { get; set; } = new ();

    public void Clear()
    {
        Ctors.Clear();
        Methods.Clear();
        Props.Clear();
    }
}

public class Program
{
    public const string ObjectType = "Object";
    public const string EnumType = "Enum";

    public static void Main(string[] args)
    {
        if (!ParseArgs(args, out var noogleArgs))
        {
            return;
        }
        ExploreLibraries(noogleArgs);
    }

    public static void ExploreLibraries(NoogleArgs args)
    {
        var libs = GetLibraries(args);
        if (libs.Count == 0)
            return;
        foreach (var path in libs)
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
            using var peFile = new PEFile(path, fs);
            var resolver = new UniversalAssemblyResolver(path, false, peFile.Metadata.DetectTargetFrameworkId());
            var typeSystem = new DecompilerTypeSystem(peFile, resolver);
            var typeInfo = new TypeInfo();
            var uniqueSignatures = new HashSet<string>();

            foreach (var type in typeSystem.MainModule.TypeDefinitions)
            {
                if (args.PublicOnly && type.Accessibility != Accessibility.Public)
                    continue;

                if (args.Type != null && type.Name != args.Type)
                    continue;

                if (type.FullName.Contains('<'))
                    continue;

                ExploreType(typeInfo, type, args, uniqueSignatures);

                typeInfo.Clear();
                uniqueSignatures.Clear();
            }
        }
    }

    private static void ExploreType(
        TypeInfo typeInfo,
        IType targetType, 
        NoogleArgs args,
        HashSet<string> uniqueSignatures)
    {
        if (targetType.Kind == TypeKind.Enum)
        {
            PrintEnum(targetType, args);
            return;
        }
        CollectTypeInfo(typeInfo, targetType, args);
        PrintTypeInfo(targetType, typeInfo, uniqueSignatures);
    }

    private static void CollectTypeInfo(TypeInfo info, 
        IType currentType, 
        NoogleArgs args)
    {
        if (args.CtorsOnly)
        {
            info.Ctors.AddRange(CollectCtors(currentType, args));
            return;
        }

        info.Ctors.AddRange(CollectCtors(currentType, args));
        info.Props.AddRange(CollectProperties(currentType, args));
        info.Methods.AddRange(CollectMethods(currentType, args));
    }

    private static IEnumerable<IMethod> CollectCtors(IType type, NoogleArgs args)
    {
        if (args.Member != null)
        {
            return Enumerable.Empty<IMethod>();
        }
        IEnumerable<IMethod> ctors = type.GetConstructors(options: GetMemberOptions.IgnoreInheritedMembers);
        if (args.PublicOnly)
            ctors = ctors.Where(c => c.Accessibility == Accessibility.Public);
        return ctors;
    }

    private static IEnumerable<IProperty> CollectProperties(IType type, NoogleArgs args)
    {
        IEnumerable<IProperty> props = type.GetProperties();
        if (args.Member != null)
            props = props.Where(p => p.Name == args.Member);
        if (args.PublicOnly)
            props = props.Where(p => p.Accessibility == Accessibility.Public);
        return props;
    }

    private static IEnumerable<IMethod> CollectMethods(IType type, NoogleArgs args)
    {
        var methods = type.GetMethods();
        methods = methods.Where(m => !m.Name.Contains('<') &&
            m.DeclaringType.Name != ObjectType &&
            m.DeclaringType.Name != EnumType
        );
        if (args.Member != null)
            methods = methods.Where(m => m.Name == args.Member);
        if (args.PublicOnly)
            methods = methods.Where(p => p.Accessibility == Accessibility.Public);
        return methods;
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
                res.Member = args[ind];
                ind++;
            }
            else if (arg == "-c")
            {
                ind++;
                res.CtorsOnly = true;
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
        res.Path ??= Directory.GetCurrentDirectory();
        return true;
    }

    private static List<string> GetLibraries(NoogleArgs args)
    {
        var res = new List<string>();
        if (args.Lib != null)
        {
            foreach (var filePath in Directory.GetFiles(args.Path))
            {
                var fileName = Path.GetFileName(filePath);
                if (string.Equals(fileName, args.Lib, StringComparison.OrdinalIgnoreCase))
                {
                    res.Add(filePath);
                    break;
                } 
            }
            return res;
        }
        foreach (var filePath in Directory.GetFiles(args.Path))
        {
            if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
            {
                res.Add(filePath);
            } 
        }
        return res;
    }

    private static void PrintInvalidArgs()
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
        tw.WriteLine("    -m <member1>:  member name (method, property, etc.)");
        tw.WriteLine("    -c:            constructors only");
        tw.WriteLine("    -a:            all accessibility (public, private, etc.)");
        tw.WriteLine("    -?:            show this message");
    }

    private static void PrintEnum(IType type, NoogleArgs args)
    {
        if (args.CtorsOnly)
            return;

        var fields = type.GetFields(f => f.IsConst && f.IsStatic && f.Accessibility == Accessibility.Public);
        foreach (var field in fields)
        {
            if (args.Member != null && field.Name != args.Member)
                continue;
            PrintEnumField(type, field);
        }
    }

    private static void PrintTypeInfo(IType type, TypeInfo info, HashSet<string> uniqueSignatures)
    {
        foreach (var prop in info.Props)
        {
            PrintProperty(type, prop, uniqueSignatures);
        }
        foreach (var ctor in info.Ctors)
        {
            PrintMethod(type, ctor, uniqueSignatures);
        }
        foreach (var method in info.Methods)
        {
            PrintMethod(type, method, uniqueSignatures);
        }
    }

    private static void PrintEnumField(
        IType type,
        IField field
    ) 
    {
        var sb = new StringBuilder();
        sb.Append(type.FullName);
        sb.Append(" ");
        sb.Append("enum");
        sb.Append(" ");
        sb.Append(type.Name);
        sb.Append(".");
        sb.Append(field.Name);
        var val = field.GetConstantValue();
        if (val != null)
        {
            sb.Append(" = ");
            sb.Append(val);
        }

        Console.WriteLine(sb.ToString());
    }

    private static void PrintProperty(
        IType type,
        IProperty property,
        HashSet<string> uniqueSignatures
    )
    {
        var returnType = property.ReturnType;
        var sb = new StringBuilder();
        sb.Append(type.FullName);
        sb.Append(" ");
        sb.Append(Access(property.Accessibility));
        sb.Append(" ");
        if (property.IsStatic) 
        {
            sb.Append("static");
            sb.Append(" ");
        }
        PrintType(sb, returnType);
        sb.Append(" ");
        sb.Append(property.Name);
        if (property.CanGet || property.CanSet)
        {
            sb.Append(" ");
            sb.Append("{");
            if (property.CanGet)
            {
                if (property.Getter.Accessibility != property.Accessibility)
                {
                    sb.Append(" ");
                    sb.Append(Access(property.Getter.Accessibility));
                }

                sb.Append(" ");
                sb.Append("get;");
            }
            if (property.CanSet)
            {
                if (property.Setter.Accessibility != property.Accessibility)
                {
                    sb.Append(" ");
                    sb.Append(Access(property.Setter.Accessibility));
                }
                sb.Append(" ");
                sb.Append("set;");
            }
            sb.Append(" ");
            sb.Append("}");
        }

        var str = sb.ToString();
        if (!uniqueSignatures.Contains(str))
        {
            uniqueSignatures.Add(str);
            Console.WriteLine(str);
        }
    }

    private static void PrintMethod(
        IType type,
        IMethod method,
        HashSet<string> uniqueSignatures
    )
    {
        var returnType = method.ReturnType;
        var parameters = method.Parameters;
        var sb = new StringBuilder();
        sb.Append(type.FullName);
        sb.Append(" ");
        sb.Append(Access(method.Accessibility));
        sb.Append(" ");
        if (method.IsStatic) 
        {
            sb.Append("static");
            sb.Append(" ");
        }
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
            {
                var val = paramInfo.GetConstantValue();
                if (val == null)
                    sb.Append(" = null");
                else 
                {
                    sb.Append(" = ");
                    sb.Append(val);
                }
            }
            sb.Append(", ");
        }
        if (parameters.Any())
        {
            sb.Remove(sb.Length - 2, 2);
        }
        sb.Append(")");
        var str = sb.ToString();
        if (!uniqueSignatures.Contains(str))
        {
            uniqueSignatures.Add(str);
            Console.WriteLine(str);
        }
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

    private static string Access(Accessibility accessibility) =>
        accessibility.ToString().ToLower();

    private static string Type(string typeName) =>
        typeName switch 
        {
            "Int32" => "int",
            "String" => "string",
            "Boolean" => "bool",
            "Object" => "object",
            "Void" => "void",
            _ => typeName
        };
}
