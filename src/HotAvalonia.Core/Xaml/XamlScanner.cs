using System.CodeDom.Compiler;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Reflection.Emit;
using HotAvalonia.Helpers;
using HotAvalonia.Reflection;

namespace HotAvalonia.Xaml;

/// <summary>
/// Provides utility methods for identifying and extracting information about Avalonia controls.
/// </summary>
public static class XamlScanner
{
    /// <summary>
    /// The expected parameter types for a valid build method.
    /// </summary>
    private static readonly Type[] s_buildSignature = [typeof(IServiceProvider)];

    /// <summary>
    /// The expected parameter types for a valid populate method.
    /// </summary>
    private static readonly Type[] s_populateSignature = [typeof(IServiceProvider), typeof(object)];

    /// <summary>
    /// Determines whether the specified assembly uses compiled bindings by default.
    /// </summary>
    /// <param name="assembly">The assembly to check for the compiled bindings metadata attribute.</param>
    /// <returns>
    /// <c>true</c> if the assembly specifies the <c>AvaloniaUseCompiledBindingsByDefault</c>
    /// metadata attribute and its value is set to <c>true</c>; otherwise, <c>false</c>.
    /// </returns>
    public static bool UsesCompiledBindingsByDefault(Assembly? assembly)
    {
        if (assembly is null)
            return false;

        foreach (AssemblyMetadataAttribute attribute in assembly.GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (!"AvaloniaUseCompiledBindingsByDefault".Equals(attribute.Key, StringComparison.Ordinal))
                continue;

            return bool.TryParse(attribute.Value, out bool value) && value;
        }

        return false;
    }

    /// <summary>
    /// Determines whether a method qualifies as a build method.
    /// </summary>
    /// <param name="method">The method to check.</param>
    /// <returns><c>true</c> if the method is a valid build method; otherwise, <c>false</c>.</returns>
    public static bool IsBuildMethod([NotNullWhen(true)] MethodBase? method)
    {
        if (method is null)
            return false;

        return method.IsConstructor && method.GetParameters().Length is 0 || MethodHelper.IsSignatureAssignableFrom(s_buildSignature, method);
    }

    /// <summary>
    /// Determines whether a method qualifies as a populate method.
    /// </summary>
    /// <param name="method">The method to check.</param>
    /// <returns><c>true</c> if the method is a valid populate method; otherwise, <c>false</c>.</returns>
    public static bool IsPopulateMethod([NotNullWhen(true)] MethodBase? method)
    {
        if (method is null)
            return false;

        return MethodHelper.IsSignatureAssignableFrom(s_populateSignature, method);
    }

    /// <summary>
    /// Determines whether a field qualifies as a populate override.
    /// </summary>
    /// <param name="field">The field to check.</param>
    /// <returns><c>true</c> if the field is a valid populate override; otherwise, <c>false</c>.</returns>
    public static bool IsPopulateOverrideField([NotNullWhen(true)] FieldInfo? field)
    {
        if (field is not { IsStatic: true, IsInitOnly: false })
            return false;

        return field.FieldType == typeof(Action<object>) || field.FieldType == typeof(Action<IServiceProvider?, object>);
    }

    /// <summary>
    /// Attempts to extract the URI associated with the XAML document
    /// represented by the given control instance.
    /// </summary>
    /// <param name="rootControl">The root control instance.</param>
    /// <param name="uri">The output parameter that receives the associated URI.</param>
    /// <returns><c>true</c> if the URI is successfully extracted; otherwise, <c>false</c>.</returns>
    public static bool TryExtractDocumentUri(object? rootControl, [NotNullWhen(true)] out Uri? uri)
        => TryExtractDocumentUri(rootControl?.GetType(), out uri);

    /// <inheritdoc cref="TryExtractDocumentUri(object?, out Uri?)"/>
    public static bool TryExtractDocumentUri(object? rootControl, [NotNullWhen(true)] out string? uri)
        => TryExtractDocumentUri(rootControl?.GetType(), out uri);

    /// <summary>
    /// Attempts to extract the URI associated with the XAML document
    /// represented by it's root control type.
    /// </summary>
    /// <param name="rootControlType">The root control type.</param>
    /// <param name="uri">The output parameter that receives the associated URI.</param>
    /// <returns><c>true</c> if the URI is successfully extracted; otherwise, <c>false</c>.</returns>
    public static bool TryExtractDocumentUri(Type? rootControlType, [NotNullWhen(true)] out Uri? uri)
    {
        if (TryExtractDocumentUri(rootControlType, out string? uriStr))
        {
            uri = new(uriStr);
            return true;
        }
        else
        {
            uri = null;
            return false;
        }
    }

    /// <inheritdoc cref="TryExtractDocumentUri(Type?, out Uri?)"/>
    public static bool TryExtractDocumentUri(Type? rootControlType, [NotNullWhen(true)] out string? uri)
    {
        uri = null;
        if (rootControlType is null)
            return false;

        MethodInfo? populate = FindPopulateControlMethod(rootControlType);
        return populate is not null && TryExtractDocumentUri(populate, out uri);
    }

    /// <summary>
    /// Attempts to extract the URI from the given populate method.
    /// </summary>
    /// <param name="populateMethod">The populate method.</param>
    /// <param name="uri">The output parameter that receives the associated URI.</param>
    /// <returns><c>true</c> if the URI is successfully extracted; otherwise, <c>false</c>.</returns>
    private static bool TryExtractDocumentUri(MethodInfo populateMethod, [NotNullWhen(true)] out string? uri)
    {
        // "Populate" methods created by Avalonia usually start like this:
        // IL_0000: ldarg.0
        // IL_0001: ldc.i4.1
        // IL_0002: newarr [System.Runtime]System.Object
        // IL_0007: dup
        // IL_0008: ldc.i4.0
        // IL_0009: ldsfld class [Avalonia.Markup.Xaml]Avalonia.Markup.Xaml.XamlIl.Runtime.IAvaloniaXamlIlXmlNamespaceInfoProvider 'CompiledAvaloniaXaml.!AvaloniaResources'/'NamespaceInfo:/FILENAME'::Singleton
        // IL_000e: castclass [System.Runtime]System.Object
        // IL_0013: stelem.ref
        // IL_0014: ldstr "avares://uri" // <-- This is what we are looking for
        const int CommonLdstrLocation = 0x14;

        uri = null;
        byte[]? methodBody = populateMethod.GetMethodBody()?.GetILAsByteArray();
        if (methodBody is null)
            return false;

        int ldstrLocation = methodBody.Length > CommonLdstrLocation && methodBody[CommonLdstrLocation] == OpCodes.Ldstr.Value
            ? CommonLdstrLocation
            : MethodBodyReader.IndexOf(methodBody, OpCodes.Ldstr.Value);

        int uriTokenLocation = ldstrLocation + 1;
        if (uriTokenLocation is 0 || uriTokenLocation + sizeof(int) > methodBody.Length)
            return false;

        try
        {
            int inlineStringToken = BitConverter.ToInt32(methodBody.AsSpan(uriTokenLocation));
            uri = populateMethod.Module.ResolveString(inlineStringToken);
            return uri is not null;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Returns compiled XAML documents located in the given assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan for pre-compiled XAML.</param>
    /// <returns>An enumerable containing compiled XAML documents.</returns>
    public static IEnumerable<CompiledXamlDocument> GetDocuments(Assembly assembly)
    {
        _ = assembly ?? throw new ArgumentNullException(nameof(assembly));

        Type? xamlLoader = assembly.GetType("CompiledAvaloniaXaml.!XamlLoader");
        MethodInfo? tryLoad = xamlLoader?.GetStaticMethods("TryLoad").OrderByDescending(x => x.GetParameters().Length).FirstOrDefault();
        byte[]? tryLoadBody = tryLoad?.GetMethodBody()?.GetILAsByteArray();
        if (tryLoad is null || tryLoadBody is null)
            return [];

        IEnumerable<CompiledXamlDocument> extractedDocuments = ExtractDocuments(tryLoadBody, tryLoad.Module);
        IEnumerable<CompiledXamlDocument> foundDocuments = FindDocuments(assembly);
        return extractedDocuments.Concat(foundDocuments).Distinct();
    }

    /// <summary>
    /// Extracts information about pre-compiled XAML from the IL of the given method body.
    /// </summary>
    /// <param name="methodBody">The IL method body to scan.</param>
    /// <param name="module">The module containing the method body.</param>
    /// <returns>An enumerable containing compiled XAML documents.</returns>
    private static IEnumerable<CompiledXamlDocument> ExtractDocuments(ReadOnlyMemory<byte> methodBody, Module module)
    {
        MethodBodyReader reader = new(methodBody);
        string? str = null;
        string? uri = null;

        while (reader.Next())
        {
            if (reader.OpCode == OpCodes.Ret)
            {
                (str, uri) = (null, null);
                continue;
            }

            if (reader.OpCode == OpCodes.Ldstr)
            {
                str = reader.ResolveString(module);
                continue;
            }

            if (reader.OpCode != OpCodes.Call && reader.OpCode != OpCodes.Newobj)
                continue;

            MethodBase method = reader.ResolveMethod(module);
            if (method.DeclaringType == typeof(string) && method.Name is nameof(string.Equals))
            {
                uri = str;
                str = null;
                continue;
            }

            if (uri is null || !IsBuildMethod(method))
                continue;

            MethodInfo? populateMethod = FindPopulateMethod(method);
            FieldInfo? populateOverrideField = FindPopulateOverrideField(method);
            Action<object> refresh = GetControlRefreshCallback(method);
            if (populateMethod is null)
                continue;

            yield return new(new(uri), method, populateMethod, populateOverrideField, refresh);
            (str, uri) = (null, null);
        }
    }

    /// <summary>
    /// Searches for compiled XAML documents located in the given assembly.
    /// </summary>
    /// <param name="assembly">The assembly to scan for pre-compiled XAML.</param>
    /// <returns>An enumerable containing compiled XAML documents.</returns>
    private static IEnumerable<CompiledXamlDocument> FindDocuments(Assembly assembly)
    {
        foreach (Type type in assembly.GetLoadedTypes())
        {
            MethodInfo? populateMethod = FindPopulateControlMethod(type);
            if (populateMethod is null)
                continue;

            if (!TryExtractDocumentUri(populateMethod, out string? uri))
                continue;

            MethodBase? buildMethod = type.GetInstanceConstructor();
            if (buildMethod is null)
                continue;

            FieldInfo? populateOverrideField = FindPopulateOverrideField(buildMethod);
            Action<object> refresh = GetControlRefreshCallback(buildMethod);
            yield return new(new(uri), buildMethod, populateMethod, populateOverrideField, refresh);
        }
    }

    /// <summary>
    /// Discovers named control references within the control associated with the given build method.
    /// </summary>
    /// <param name="buildMethod">The build method associated with the control scope to search within.</param>
    /// <returns>An enumerable containing discovered named control references.</returns>
    private static IEnumerable<NamedControlReference> FindNamedControlReferences(MethodBase buildMethod)
    {
        if (buildMethod is not { IsConstructor: true, DeclaringType: Type declaringType })
            return [];

        MethodInfo? initializeComponent = declaringType
            .GetInstanceMethods("InitializeComponent")
            .OrderByDescending(static x => x.IsGeneratedByAvalonia())
            .ThenByDescending(static x => x.GetParameters().Length)
            .FirstOrDefault(static x => x.ReturnType == typeof(void));

        byte[]? initializeComponentBody = initializeComponent?.GetMethodBody()?.GetILAsByteArray();
        if (initializeComponent is null || initializeComponentBody is null)
            return [];

        return ExtractNamedControlReferences(initializeComponentBody, initializeComponent.Module);
    }

    /// <summary>
    /// Extracts named control references from the IL of the given method body.
    /// </summary>
    /// <param name="methodBody">The IL method body to scan.</param>
    /// <param name="module">The module containing the method body.</param>
    /// <returns>An enumerable containing extracted named control references.</returns>
    private static IEnumerable<NamedControlReference> ExtractNamedControlReferences(ReadOnlyMemory<byte> methodBody, Module module)
    {
        _ = module ?? throw new ArgumentNullException(nameof(module));

        MethodBodyReader reader = new(methodBody);
        while (reader.Next())
        {
            if (reader.OpCode != OpCodes.Ldstr)
                continue;

            string name = reader.ResolveString(module);
            if (!reader.Next() || reader.OpCode != OpCodes.Call)
                continue;

            MethodBase findMethod = reader.ResolveMethod(module);
            if (!reader.Next() || reader.OpCode != OpCodes.Stfld)
                continue;

            // T Avalonia.Controls.NameScopeExtensions.Find<T>(INameScope, string)
            FieldInfo field = reader.ResolveField(module);
            if (!"Find".Equals(findMethod.Name, StringComparison.Ordinal))
                continue;

            Type[] genericArguments = findMethod.IsGenericMethod ? findMethod.GetGenericArguments() : Type.EmptyTypes;
            Type controlType = genericArguments.Length > 0 && field.FieldType.IsAssignableFrom(genericArguments[genericArguments.Length - 1])
                ? genericArguments[genericArguments.Length - 1]
                : field.FieldType;

            yield return new(name, controlType, field);
        }
    }

    /// <summary>
    /// Finds all parameterless instance methods within the specified control
    /// that are decorated with the <c>AvaloniaHotReloadAttribute</c>.
    /// </summary>
    /// <param name="userControlType">The type to inspect for hot reload callback methods.</param>
    /// <returns>
    /// A collection of <see cref="MethodInfo"/> representing all parameterless instance methods
    /// within the provided control that are decorated with the <c>AvaloniaHotReloadAttribute</c>.
    /// </returns>
    private static IEnumerable<MethodInfo> FindAvaloniaHotReloadCallbacks(MethodBase buildMethod)
    {
        if (buildMethod is not { IsConstructor: true, DeclaringType: Type declaringType })
            return [];

        return declaringType
            .GetInstanceMethods()
            .Where(static x => x.GetParameters().Length == 0)
            .Where(static x => x.GetCustomAttributes(inherit: true)
                .Any(static y => "HotAvalonia.AvaloniaHotReloadAttribute".Equals(y?.GetType().FullName, StringComparison.Ordinal)));
    }

    /// <summary>
    /// Constructs a combined refresh callback for a control, aggregating
    /// hot reload methods and named control refresh actions.
    /// </summary>
    /// <param name="buildMethod">The build method associated with the control.</param>
    /// <returns>
    /// A delegate that, when invoked, executes all associated refresh actions for
    /// the control, including hot reload callbacks and named control refresh methods.
    /// </returns>
    private static Action<object> GetControlRefreshCallback(MethodBase buildMethod)
    {
        Action<object>[] callbacks = FindNamedControlReferences(buildMethod)
            .Select(static x => (Action<object>)x.Refresh)
            .Concat(FindAvaloniaHotReloadCallbacks(buildMethod)
            .Select(static x => x.CreateUnsafeDelegate<Action<object>>()))
            .ToArray();

        if (callbacks.Length == 0)
            return static x => { };

        return (Action<object>)Delegate.Combine(callbacks);
    }

    /// <summary>
    /// Finds the populate override field in relation to the given build method.
    /// </summary>
    /// <param name="buildMethod">The build method for which the field is sought.</param>
    /// <returns>The <see cref="MethodInfo"/> object representing the field, or <c>null</c> if not found.</returns>
    private static FieldInfo? FindPopulateOverrideField(MethodBase buildMethod)
    {
        if (buildMethod.DeclaringType is not Type declaringType)
            return null;

        int separatorIndex = buildMethod.Name.IndexOf(':');
        string populateName = separatorIndex >= 0
            ? $"PopulateOverride{buildMethod.Name.Substring(separatorIndex)}"
            : "!XamlIlPopulateOverride";

        FieldInfo? field = declaringType.GetStaticField(populateName);
        return IsPopulateOverrideField(field) ? field : null;
    }

    /// <summary>
    /// Finds the populate method in relation to the given build method.
    /// </summary>
    /// <param name="buildMethod">The build method for which the populate method is sought.</param>
    /// <returns>The <see cref="MethodInfo"/> object representing the populate method, or <c>null</c> if not found.</returns>
    private static MethodInfo? FindPopulateMethod(MethodBase buildMethod)
    {
        if (buildMethod.DeclaringType is not Type declaringType)
            return null;

        int separatorIndex = buildMethod.Name.IndexOf(':');
        if (separatorIndex < 0)
            return FindPopulateControlMethod(declaringType);

        string populateName = $"Populate{buildMethod.Name.Substring(separatorIndex)}";
        return declaringType.GetStaticMethods(populateName).FirstOrDefault(IsPopulateMethod);
    }

    /// <summary>
    /// Finds the populate method for a user control.
    /// </summary>
    /// <param name="userControlType">The type of the user control for which the populate method is sought.</param>
    /// <returns>The <see cref="MethodInfo"/> object representing the populate method, or <c>null</c> if not found.</returns>
    private static MethodInfo? FindPopulateControlMethod(Type userControlType)
        => userControlType.GetStaticMethod("!XamlIlPopulate", [typeof(IServiceProvider), userControlType]);

    /// <summary>
    /// Determines whether the specified member is generated by Avalonia.
    /// </summary>
    /// <param name="member">The member to check.</param>
    /// <returns>
    /// <c>true</c> if the specified member is generated by Avalonia;
    /// otherwise, <c>false</c>.
    /// </returns>
    public static bool IsGeneratedByAvalonia(this MemberInfo member)
    {
        GeneratedCodeAttribute? generatedCodeAttribute = member?.GetCustomAttribute<GeneratedCodeAttribute>();
        if (generatedCodeAttribute is not { Tool: not null })
            return false;

        return generatedCodeAttribute.Tool.StartsWith("Avalonia.Generators.", StringComparison.Ordinal);
    }
}
