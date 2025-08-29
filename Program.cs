using System.Text;
using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using System.Reflection;
using ICSharpCode.Decompiler.Metadata;
using ICSharpCode.Decompiler.TypeSystem;

namespace noogle;

public class NoogleArgs
{
    public string[] Paths { get; set; }
    public string Type { get; set; }
    public string Member { get; set; }
    public string Lib { get; set; }
    public bool CtorsOnly { get; set; }
    public bool Stat { get; set; }
    public bool PublicOnly { get; set; } = true;
    public bool IncludeInherited { get; set; }
}

public class Stat
{
    public int LineCount { get; set; }
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
    private const string ObjectType = "Object";
    private const string AttributeType = "Attribute";
    private const string EnumType = "Enum";

    private static List<(string, string)> _shortTypes = [
        ("bool", "Boolean"),
        ("byte", "Byte"),
        ("sbyte", "SByte"),
        ("char", "Char"),
        ("decimal", "Decimal"),
        ("double", "Double"),
        ("float", "Single"),
        ("int", "Int32"),
        ("uint", "UInt32"),
        ("nint", "IntPtr"),
        ("nuint", "UIntPtr"),
        ("long", "Int64"),
        ("ulong", "UInt64"),
        ("short", "Int16"),
        ("ushort", "UInt16"),
        ("object", "Object"),
        ("string", "String"),
        ("delegate", "Delegate"),
        ("void", "Void"),
    ];
    private static Dictionary<string, string> _toShortName = _shortTypes.ToDictionary(x => x.Item2, x => x.Item1);
    private static Dictionary<string, string> _fromShortName = _shortTypes.ToDictionary(x => x.Item1, x => x.Item2);

    public static async Task Main(string[] args)
    {
        if (!ParseArgs(args, out var noogleArgs))
        {
            return;
        }
        await ExploreLibrariesAsync(noogleArgs);
    }

    private static async Task ExploreLibrariesAsync(NoogleArgs args)
    {
        var libs = GetLibraries(args);
        if (libs.Count == 0)
            return;
        var outStat = new ConcurrentBag<(string, int)>();
        await Task.WhenAll(libs.Select(async path => {
            try 
            {
                await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read);
                using var peFile = new PEFile(path, fs);
                var resolver = new UniversalAssemblyResolver(path, false, peFile.Metadata.DetectTargetFrameworkId());
                var typeSystem = await DecompilerTypeSystem.CreateAsync(peFile, resolver);
                var typeInfo = new TypeInfo();
                var stat = new Stat();
                var outLines = new StringBuilder();

                foreach (var type in typeSystem.MainModule.TypeDefinitions)
                {
                    if (args.PublicOnly && type.Accessibility != Accessibility.Public)
                        continue;

                    if (args.Type != null && type.Name != args.Type)
                        continue;

                    if (type.FullName.Contains('<'))
                        continue;

                    ExploreType(typeInfo, type, args, stat, outLines);
                    await Console.Out.WriteAsync(outLines);

                    typeInfo.Clear();
                    outLines.Clear();
                }
                if (args.Stat)
                {
                    var fileName = Path.GetFileName(path);
                    outStat.Add((fileName, stat.LineCount));
                }
            } catch (MetadataFileNotSupportedException){}
        }));
        if (args.Stat)
        {
            PrintStat(outStat);
        }
    }

    private static void PrintStat(IEnumerable<(string, int)> outStat)
    {
        foreach (var (lib, count) in outStat.OrderBy(x => x.Item2))
        {
            Console.Write(lib);
            Console.Write(" : ");
            Console.Write(count);
            Console.WriteLine();
        }
    }

    private static void ExploreType(
        TypeInfo typeInfo,
        IType targetType, 
        NoogleArgs args,
        Stat stat,
        StringBuilder sb
    )
    {
        if (targetType.Kind == TypeKind.Enum)
        {
            PrintEnum(targetType, args, stat, sb);
            return;
        }
        CollectTypeInfo(typeInfo, targetType, args);
        PrintTypeInfo(targetType, typeInfo, stat, sb);
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
        IEnumerable<IMethod> ctors = type.GetConstructors();
        if (args.PublicOnly)
            ctors = ctors.Where(c => c.Accessibility == Accessibility.Public);
        return ctors;
    }

    private static IEnumerable<IProperty> CollectProperties(IType type, NoogleArgs args)
    {
        IEnumerable<IProperty> props = type.GetProperties(options: MemberOptions(args));
        if (args.Member != null)
            props = props.Where(p => p.Name == args.Member);
        if (args.PublicOnly)
            props = props.Where(p => p.Accessibility == Accessibility.Public);
        return props;
    }

    private static IEnumerable<IMethod> CollectMethods(IType type, NoogleArgs args)
    {
        var methods = type.GetMethods(options: MemberOptions(args));
        methods = methods.Where(m => !m.Name.Contains('<') &&
            m.DeclaringType.Name != ObjectType &&
            m.DeclaringType.Name != EnumType &&
            m.DeclaringType.Name != AttributeType
        );
        if (args.Member != null)
            methods = methods.Where(m => m.Name == args.Member);
        if (args.PublicOnly)
            methods = methods.Where(p => p.Accessibility == Accessibility.Public);
        return methods;
    }

    private static GetMemberOptions MemberOptions(NoogleArgs args) =>
        args.IncludeInherited ? GetMemberOptions.None : GetMemberOptions.IgnoreInheritedMembers;

    private static bool ParseArgs(string[] args, out NoogleArgs res)
    {
        res = new NoogleArgs();
        var ind = 0;
        while (ind < args.Length)
        {
            var arg = args[ind];
            if (arg == "-p" && ind < args.Length - 1)
            {
                ind++;
                res.Paths = args[ind].Split(';', StringSplitOptions.None);
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
                res.Type = LongType(args[ind]);
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
            else if (arg == "-i")
            {
                ind++;
                res.IncludeInherited = true;
            } 
            else if (arg == "-s")
            {
                ind++;
                res.Stat = true;
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
        res.Paths ??= [Directory.GetCurrentDirectory()];
        return true;
    }

    private static List<string> GetLibraries(NoogleArgs args)
    {
        var res = new List<string>();
        var fileNames = new HashSet<string>();
        var files = new List<string>();
        foreach (var path in args.Paths)
        {
            foreach (var filePath in Directory.GetFiles(path))
            {
                var fileName = Path.GetFileName(filePath);
                if (fileNames.Add(fileName))
                    files.Add(filePath);
            }
        }
        if (args.Lib != null)
        {
            foreach (var filePath in files)
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
        var ignoreList = GetIgnoreList();
        foreach (var filePath in files)
        {
            var fileName = Path.GetFileName(filePath);
            if (filePath.EndsWith(".dll", StringComparison.OrdinalIgnoreCase) &&
                !ignoreList.IsMatch(fileName))
            {
                res.Add(filePath);
            } 
        }
        return res;
    }

    private static Regex GetIgnoreList()
    {
        var exePath = AppDomain.CurrentDomain.BaseDirectory;
        var exeDir = Path.GetDirectoryName(exePath);
        var ignoreFile = Path.Combine(exeDir, ".noogleignore");
        var expr = string.Join('|', File.ReadAllLines(ignoreFile).Where(s => !string.IsNullOrEmpty(s)));
        return new Regex(expr);
    }

    private static void PrintInvalidArgs()
    {
        var tw = Console.Error;
        tw.WriteLine("Invalid argument list");
        tw.WriteLine();
        PrintUsage(tw);
    }

    private static void PrintUsage(TextWriter tw)
    {
        tw.WriteLine("noogle [args]:");
        tw.WriteLine("    -p <path1,path2 ...>: path(s)");
        tw.WriteLine("    -l <library1>:        library");
        tw.WriteLine("    -t <type1>:           type name");
        tw.WriteLine("    -m <member1>:         member name (method, property, etc.)");
        tw.WriteLine("    -c:                   constructors only");
        tw.WriteLine("    -a:                   all accessibility (public, private, etc.)");
        tw.WriteLine("    -i:                   include inherited members");
        tw.WriteLine("    -s:                   print statistics");
        tw.WriteLine("    -?:                   show this message");
    }

    private static void PrintEnum(IType type, NoogleArgs args, Stat stat, StringBuilder sb)
    {
        if (args.CtorsOnly)
            return;

        var fields = type.GetFields(f => f.IsConst && f.IsStatic && f.Accessibility == Accessibility.Public);
        foreach (var field in fields)
        {
            if (args.Member != null && field.Name != args.Member)
                continue;
            PrintEnumField(type, field, sb);
            stat.LineCount++;
        }
    }

    private static void PrintTypeInfo(IType type, TypeInfo info, Stat stat, StringBuilder sb)
    {
        foreach (var prop in info.Props)
        {
            PrintProperty(type, prop, sb);
            stat.LineCount++;
        }
        foreach (var ctor in info.Ctors)
        {
            PrintMethod(type, ctor, sb);
            stat.LineCount++;
        }
        foreach (var method in info.Methods)
        {
            PrintMethod(type, method, sb);
            stat.LineCount++;
        }
    }

    private static void PrintEnumField(
        IType type,
        IField field,
        StringBuilder sb
    ) 
    {
        sb.Append(type.FullName);
        sb.Append(" ");
        sb.Append("enum");
        sb.Append(" ");
        sb.Append(type.Name);
        sb.Append(".");
        sb.Append(field.Name);
        var val = ConstantVal(field.GetConstantValue());
        if (val != null)
        {
            sb.Append(" = ");
            sb.Append(val);
        }

        sb.AppendLine();
    }

    private static void PrintProperty(
        IType type,
        IProperty property,
        StringBuilder sb
    )
    {
        var returnType = property.ReturnType;
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

        sb.AppendLine();
    }

    private static void PrintMethod(
        IType type,
        IMethod method,
        StringBuilder sb
    )
    {
        var returnType = method.ReturnType;
        var parameters = method.Parameters;
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
                var val = ConstantVal(paramInfo.GetConstantValue());
                sb.Append(" = ");
                sb.Append(val);
            }
            sb.Append(", ");
        }
        if (parameters.Any())
        {
            sb.Remove(sb.Length - 2, 2);
        }
        sb.Append(")");
        sb.AppendLine();
    }

    private static void PrintType(StringBuilder sb, IType type)
    {
        sb.Append(ShortType(type.Name));
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

    private static string ConstantVal(object? val)
    {
        if (val == null)
            return "null";

        if (val is char)
        {
            return string.Format("'{0}'", ToCharLiteral((char)val));
        } 
        else if (val is string)
        {
            return string.Format("\"{0}\"", ToStringLiteral((string) val));
        } 
        else if (val is bool)
        {
            return (bool)val ? "true" : "false";
        }
        var str = val.ToString();
        return str;
    }

    private static string ToCharLiteral(char valueTextForCompiler)
    {
        return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(valueTextForCompiler, false);
    }

    private static string ToStringLiteral(string valueTextForCompiler)
    {
        return Microsoft.CodeAnalysis.CSharp.SymbolDisplay.FormatLiteral(valueTextForCompiler, false);
    }

    private static string Access(Accessibility accessibility) =>
        accessibility.ToString().ToLower();

    private static string LongType(string shortName)
    {
        if (_fromShortName.TryGetValue(shortName, out var longName))
            return longName;
        return shortName;
    }

    private static string ShortType(string longName)
    {
        if (_toShortName.TryGetValue(longName, out var shortName))
            return shortName;
        return longName;
    }
}
