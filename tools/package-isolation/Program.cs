using System.Reflection;
using System.Text.Json;
using DotNetJq;

AssertDefaultOutputs("map(. * 2) | add", "[1,2,3]", "12");
AssertDefaultOutputs(
    ".users[] | select(.active) | .name",
    """{"users":[{"name":"Ada","active":true},{"name":"Linus","active":false}]}""",
    "\"Ada\"");
AssertDefaultOutputs("[range(0;4)]", "null", "[0,1,2,3]");
AssertDefaultOutputs("9E999999999", "null", "9E+999999999");
AssertDefaultOutputs(
    "[(0.5|gamma),(0.5|lgamma),(0.5|lgamma_r),(0.5|tgamma)]",
    "null",
    "[0.5723649429247001,0.5723649429247001,[0.5723649429247001,1],1.772453850905516]");
VerifyGlibcCompatBoundary();

var explicitEnvironment = new JqExecutionOptions
{
    Environment = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNETJQ_EXPLICIT"] = "visible",
    },
};
AssertOutputs("$ENV", "null", explicitEnvironment, "{\"DOTNETJQ_EXPLICIT\":\"visible\"}");
AssertOutputs("env", "null", explicitEnvironment, "{\"DOTNETJQ_EXPLICIT\":\"visible\"}");
AssertDefaultOutputs(
    "[$ENV.DOTNETJQ_ISOLATION_SENTINEL,env.DOTNETJQ_ISOLATION_SENTINEL]",
    "null",
    "[\"visible\",\"visible\"]");

Expect<JqRuntimeException>(
    () => JqProgram.Compile(".").Execute("\"é\"", new JqExecutionOptions { MaxInputBytes = 3 }),
    "input-byte");
Expect<JqRuntimeException>(
    () => JqProgram.Compile(".[]").Execute("[1,2]", new JqExecutionOptions { MaxOutputValues = 1 }),
    "output-value");
Expect<JqRuntimeException>(
    () => JqProgram.Compile(".").Execute("\"é\"", new JqExecutionOptions { MaxOutputBytes = 3 }),
    "output-byte");

using (var cancellation = new CancellationTokenSource())
{
    cancellation.Cancel();
    Expect<OperationCanceledException>(
        () => JqProgram.Compile(".").Execute(
            "not-json",
            new JqExecutionOptions { CancellationToken = cancellation.Token }));
}

Console.WriteLine("PACKAGE_ISOLATION_OK");

static void VerifyGlibcCompatBoundary()
{
    var mainAssembly = typeof(JqProgram).Assembly;
    var componentReference = mainAssembly.GetReferencedAssemblies()
        .SingleOrDefault(static reference =>
            reference.Name == "DotNetJq.GlibcCompat") ??
        throw new InvalidOperationException(
            "DotNetJq.dll does not dynamically link the GlibcCompat assembly.");
    if (componentReference.Version != new Version(1, 0, 0, 0))
    {
        throw new InvalidOperationException(
            $"DotNetJq.dll references unexpected GlibcCompat assembly version " +
            $"{componentReference.Version}.");
    }

    var componentAssembly = Assembly.Load(componentReference);
    if (componentAssembly == mainAssembly)
    {
        throw new InvalidOperationException(
            "GlibcCompat was embedded in DotNetJq.dll instead of remaining replaceable.");
    }

    if (mainAssembly.GetTypes().Any(static type =>
        type.Namespace == "DotNetJq.GlibcCompat"))
    {
        throw new InvalidOperationException(
            "DotNetJq.dll unexpectedly defines a GlibcCompat implementation type.");
    }

    var componentType = componentAssembly.GetType(
        "DotNetJq.GlibcCompat.GlibcCompatMath",
        throwOnError: true)!;
    var exportedTypes = componentAssembly.GetExportedTypes();
    if (exportedTypes.Length != 1 || exportedTypes[0] != componentType)
    {
        throw new InvalidOperationException(
            "GlibcCompat public type surface differs from the single documented " +
            "DotNetJq.GlibcCompat.GlibcCompatMath type: " +
            string.Join(", ", exportedTypes.Select(static type => type.FullName)));
    }

    var declaredPublicMembers = componentType.GetMembers(
        BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance |
        BindingFlags.DeclaredOnly);
    var declaredPublicMethods = declaredPublicMembers.OfType<MethodInfo>().ToArray();
    if (declaredPublicMembers.Length != 4 ||
        declaredPublicMethods.Length != 4 ||
        declaredPublicMethods.Any(static method => !IsExpectedGlibcCompatMethod(method)))
    {
        throw new InvalidOperationException(
            "GlibcCompat public ABI differs from the four documented static methods: " +
            string.Join(", ", declaredPublicMembers.Select(DescribeMember)));
    }

    var gamma = RequireMethod(componentType, "Gamma", typeof(double));
    var lgamma = RequireMethod(componentType, "Lgamma", typeof(double));
    var lgammaR = RequireMethod(componentType, "LgammaR", typeof((double, int)));
    var tgamma = RequireMethod(componentType, "Tgamma", typeof(double));

    AssertExact((double)gamma.Invoke(null, [0.5])!, 0.5723649429247001, "Gamma");
    AssertExact((double)lgamma.Invoke(null, [0.5])!, 0.5723649429247001, "Lgamma");
    var signed = ((double Value, int Sign))lgammaR.Invoke(null, [0.5])!;
    AssertExact(signed.Value, 0.5723649429247001, "LgammaR.Value");
    if (signed.Sign != 1)
    {
        throw new InvalidOperationException($"LgammaR.Sign returned {signed.Sign}, expected 1.");
    }

    AssertExact((double)tgamma.Invoke(null, [0.5])!, 1.772453850905516, "Tgamma");

    foreach (var assembly in new[] { mainAssembly, componentAssembly })
    {
        if (assembly.GetReferencedAssemblies().Any(static reference =>
            reference.Name == "System.Diagnostics.Process"))
        {
            throw new InvalidOperationException(
                $"{assembly.GetName().Name} unexpectedly references System.Diagnostics.Process.");
        }

        var pinvokeMethod = assembly.GetTypes()
            .SelectMany(static type => type.GetMethods(
                BindingFlags.Public | BindingFlags.NonPublic |
                BindingFlags.Static | BindingFlags.Instance))
            .FirstOrDefault(static method =>
                (method.Attributes & MethodAttributes.PinvokeImpl) != 0);
        if (pinvokeMethod is not null)
        {
            throw new InvalidOperationException(
                $"{assembly.GetName().Name} unexpectedly contains P/Invoke method " +
                $"{pinvokeMethod.DeclaringType?.FullName}.{pinvokeMethod.Name}.");
        }
    }

    var expectedInformationalVersion = Environment.GetEnvironmentVariable(
        "DOTNETJQ_EXPECT_GLIBC_INFORMATIONAL_VERSION");
    if (expectedInformationalVersion is not null)
    {
        var actualInformationalVersion = componentAssembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion;
        if (actualInformationalVersion != expectedInformationalVersion)
        {
            throw new InvalidOperationException(
                $"Expected replacement GlibcCompat informational version " +
                $"{JsonSerializer.Serialize(expectedInformationalVersion)}, but loaded " +
                $"{JsonSerializer.Serialize(actualInformationalVersion)}.");
        }
    }
}

static bool IsExpectedGlibcCompatMethod(MethodInfo method)
{
    var parameters = method.GetParameters();
    if (!method.IsStatic ||
        parameters.Length != 1 ||
        parameters[0].ParameterType != typeof(double))
    {
        return false;
    }

    return method.Name switch
    {
        "Gamma" or "Lgamma" or "Tgamma" => method.ReturnType == typeof(double),
        "LgammaR" => method.ReturnType == typeof((double, int)),
        _ => false,
    };
}

static string DescribeMember(MemberInfo member) => member is MethodInfo method
    ? $"{method.ReturnType.Name} {method.Name}(" +
      string.Join(",", method.GetParameters().Select(static parameter => parameter.ParameterType.Name)) +
      ")"
    : $"{member.MemberType} {member.Name}";

static MethodInfo RequireMethod(Type type, string name, Type returnType)
{
    var method = type.GetMethod(
        name,
        BindingFlags.Public | BindingFlags.Static,
        binder: null,
        [typeof(double)],
        modifiers: null);
    if (method is null || method.ReturnType != returnType)
    {
        throw new InvalidOperationException(
            $"GlibcCompat ABI method {name}(double) -> {returnType} is missing.");
    }

    return method;
}

static void AssertExact(double actual, double expected, string operation)
{
    if (BitConverter.DoubleToUInt64Bits(actual) !=
        BitConverter.DoubleToUInt64Bits(expected))
    {
        throw new InvalidOperationException(
            $"{operation} returned {actual:R}, expected exact {expected:R}.");
    }
}

static void AssertDefaultOutputs(string filter, string input, params string[] expected) =>
    AssertOutputs(filter, input, JqExecutionOptions.Default, expected);

static void AssertOutputs(
    string filter,
    string input,
    JqExecutionOptions options,
    params string[] expected)
{
    var actual = JqProgram.Compile(filter)
        .Execute(input, options)
        .Select(static value => value.GetRawText())
        .ToArray();

    if (!actual.SequenceEqual(expected, StringComparer.Ordinal))
    {
        throw new InvalidOperationException(
            $"Filter {JsonSerializer.Serialize(filter)} expected " +
            $"[{string.Join(", ", expected.Select(static value => JsonSerializer.Serialize(value)))}] " +
            $"but produced " +
            $"[{string.Join(", ", actual.Select(static value => JsonSerializer.Serialize(value)))}].");
    }
}

static void Expect<TException>(Action action, string? messageFragment = null)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException exception)
    {
        if (messageFragment is not null &&
            !exception.Message.Contains(messageFragment, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Expected {typeof(TException).Name} containing {JsonSerializer.Serialize(messageFragment)}, " +
                $"but got {JsonSerializer.Serialize(exception.Message)}.");
        }

        return;
    }

    throw new InvalidOperationException($"Expected {typeof(TException).Name}, but no exception was thrown.");
}
