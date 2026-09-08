// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/linker.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/linker.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/linker.c.cs
// Substitutions: module probing and loading read through the explicit JqModuleResolver filesystem capability.
// Known differences: physical path diagnostics are normalized by the configured filesystem capability;
// cancellation/resource limits are explicit managed host controls around the native-shaped block linker.

using System.Text;
using DotNetJq;
using DotNetJq.Compatibility.FileSystem;

namespace DotNetJq.Port;

internal static partial class libjq
{
    // jq-1.8.2 src/linker.c:find_lib() advances to the next layout only when
    // stat(2) reports ENOENT. A directory is still an existing candidate, as
    // is a regular file rejected only by the managed size policy; their later
    // load reports the error. Every other probe failure abandons this search
    // root. Keep both public resolution and the direct jv linker on this one
    // transition function so their candidate order cannot diverge again.
    private static JqFileReadResult? probe_lib_search_root(
        JqModuleResolver resolver,
        IReadOnlyList<string> candidates)
    {
        foreach (var candidate in candidates)
        {
            resolver.ThrowIfCancellationRequested();
            var read = resolver.FileSystem.ReadFile(candidate);
            resolver.ThrowIfCancellationRequested();
            switch (read.Status)
            {
                case JqFileReadStatus.Success:
                case JqFileReadStatus.IsDirectory:
                case JqFileReadStatus.TooLarge:
                    return read;

                case JqFileReadStatus.NotFound:
                    continue;

                case JqFileReadStatus.AccessDenied:
                case JqFileReadStatus.InvalidPath:
                case JqFileReadStatus.Error:
                    return null;

                default:
                    throw new InvalidOperationException(
                        $"Unknown jq file-read status: {read.Status}.");
            }
        }

        return null;
    }

    private static string[] build_lib_candidates(
        string searchPath,
        string relativePath,
        string suffix,
        string basename) =>
        [
            CombineSearchPath(searchPath, relativePath + suffix),
            CombineSearchPath(searchPath, relativePath + "/jq/main" + suffix),
            CombineSearchPath(searchPath, relativePath + "/" + basename + suffix),
        ];

    private static string module_read_failure(JqFileReadResult read)
    {
        var detail = read.Status == JqFileReadStatus.IsDirectory
            ? "It's a directory"
            : read.ErrorMessage ?? "unknown error";
        return $"Could not open {read.Path}: {detail}";
    }

    internal static bool path_is_relative(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        return !Path.IsPathFullyQualified(path);
    }

    internal static JqModuleResolution find_lib(
        JqModuleResolver resolver,
        string relativePath,
        IEnumerable<string>? searchPaths,
        JqModuleFileKind kind,
        string? libraryOrigin,
        bool includeCurrentDirectory)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        resolver.ThrowIfCancellationRequested();
        if (relativePath is null)
        {
            return JqModuleResolution.Failure("Module path must be a string");
        }

        var search = jv_invalid();
        try
        {
            if (searchPaths is null && includeCurrentDirectory)
            {
                search = default_search(resolver, search);
            }
            else
            {
                jv_free(search);
                search = jv_array();
                var paths = searchPaths ?? resolver.LibraryPaths;
                foreach (var path in paths)
                {
                    resolver.ThrowIfCancellationRequested();
                    if (path is not null)
                    {
                        search = jv_array_append(search, jv_string(path));
                    }
                }
            }
        }
        catch
        {
            jv_free(search);
            throw;
        }

        var suffix = kind == JqModuleFileKind.Module ? ".jq" : ".json";
        var resolved = find_lib(
            resolver,
            validate_relpath(jv_string(relativePath)),
            search,
            suffix,
            jv_string(resolver.JqOrigin),
            libraryOrigin is null ? jv_null() : jv_string(libraryOrigin),
            out var selectedRead);
        search = jv_invalid();
        try
        {
            if (!resolved.IsValid)
            {
                var message = jv_invalid_has_msg(jv_copy(resolved))
                    ? jv_invalid_get_msg(resolved)
                    : jv_string("module not found: " + relativePath);
                resolved = jv_invalid();
                try
                {
                    return JqModuleResolution.Failure(message.StringValue);
                }
                finally
                {
                    jv_free(message);
                }
            }

            if (selectedRead is not { } candidate)
            {
                throw new InvalidOperationException(
                    "find_lib returned a path without its capability-read probe result.");
            }

            return candidate.IsSuccess
                ? JqModuleResolution.Success(
                    new JqModuleFile(candidate.Path, candidate.Contents))
                : JqModuleResolution.Failure(module_read_failure(candidate));
        }
        finally
        {
            jv_free(search);
            jv_free(resolved);
        }
    }

    internal static JqModuleResolution load_module_meta(
        JqModuleResolver resolver,
        string moduleRelativePath) =>
        find_lib(
            resolver,
            moduleRelativePath,
            resolver.LibraryPaths,
            JqModuleFileKind.Module,
            libraryOrigin: null,
            includeCurrentDirectory: false);

    internal static string? validate_relpath(string relativePath)
    {
        if (relativePath is null)
        {
            return "Module path must be a string";
        }

        if (relativePath.Contains('\0', StringComparison.Ordinal))
        {
            return "Module path contains a NUL byte";
        }

        if (relativePath.Contains('\\', StringComparison.Ordinal))
        {
            return $"Modules must be named by relative paths using '/', not '\\' ({relativePath})";
        }

        var components = relativePath.Split('/');
        for (var index = 0; index < components.Length; index++)
        {
            if (components[index] == "..")
            {
                return $"Relative paths to modules may not traverse to parent directories ({relativePath})";
            }

            if (index > 0 && string.Equals(
                    components[index],
                    components[index - 1],
                    StringComparison.Ordinal))
            {
                return $"module names must not have equal consecutive components: {relativePath}";
            }
        }

        return null;
    }

    private static string CombineSearchPath(string searchPath, string relativePath)
    {
        // Do not use Path.Combine here: upstream always appends the module name,
        // even when it begins with '/', so a module cannot replace its search root.
        return searchPath.TrimEnd('/', '\\') + "/" + relativePath.TrimStart('/');
    }

    // jq-1.8.2 src/linker.c:path_is_relative(). The jv-shaped overload preserves
    // the native ownership contract: p is consumed on every return path.
    internal static int path_is_relative(jv p)
    {
        try
        {
            if (p.Kind != jv_kind.JV_KIND_STRING)
            {
                throw new ArgumentException("Module path must be a string", nameof(p));
            }

            return path_is_relative(p.StringValue) ? 1 : 0;
        }
        finally
        {
            jv_free(p);
        }
    }

    // jq-1.8.2 src/linker.c:validate_relpath(). Success returns the same owned
    // string; failure consumes it and returns an invalid carrying the source message.
    internal static jv validate_relpath(jv name)
    {
        if (name.Kind != jv_kind.JV_KIND_STRING)
        {
            jv_free(name);
            return jv_invalid_with_msg(jv_string("Module path must be a string"));
        }

        var error = validate_relpath(name.StringValue);
        if (error is null)
        {
            return name;
        }

        jv_free(name);
        return jv_invalid_with_msg(jv_string(error));
    }

    // jq-1.8.2 src/linker.c:jv_basename(). This helper consumes name and
    // returns either that owner or a newly allocated final path component.
    internal static jv jv_basename(jv name)
    {
        if (name.Kind != jv_kind.JV_KIND_STRING)
        {
            jv_free(name);
            return jv_invalid_with_msg(jv_string("Module path must be a string"));
        }

        var value = name.StringValue;
        var separator = value.LastIndexOf('/');
        if (separator < 0)
        {
            return name;
        }

        // The C helper starts at the slash itself; find_lib immediately passes
        // the result through jq_realpath, which removes the resulting doubled
        // separator. Preserve that intermediate source shape here.
        var result = jv_string(value[separator..]);
        jv_free(name);
        return result;
    }

    // jq-1.8.2 src/linker.c:default_search(). The explicit resolver is the one
    // managed substitution: it supplies jq_get_lib_dirs without ambient access.
    internal static jv default_search(JqModuleResolver resolver, jv value)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        resolver.ThrowIfCancellationRequested();
        if (!value.IsValid)
        {
            jv_free(value);
            var search = jv_array_append(jv_array(), jv_string("."));
            foreach (var path in resolver.LibraryPaths)
            {
                resolver.ThrowIfCancellationRequested();
                search = jv_array_append(search, jv_string(path));
            }

            return search;
        }

        if (value.Kind != jv_kind.JV_KIND_ARRAY)
        {
            return jv_array_append(jv_array(), value);
        }

        return value;
    }

    // jq-1.8.2 src/linker.c:build_lib_search_chain(). All jv arguments are
    // consumed. The return is [expanded-paths, last-expansion-error].
    internal static jv build_lib_search_chain(
        JqModuleResolver resolver,
        jv searchPath,
        jv jqOrigin,
        jv libraryOrigin)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        if (searchPath.Kind != jv_kind.JV_KIND_ARRAY)
        {
            jv_free(searchPath);
            jv_free(jqOrigin);
            jv_free(libraryOrigin);
            return jv_array_append(
                jv_array_append(jv_array(), jv_array()),
                jv_invalid_with_msg(jv_string("Module search path must be an array")));
        }

        var expanded = jv_array();
        var error = jv_null();
        try
        {
            var length = jv_array_length(jv_copy(searchPath));
            for (var index = 0; index < length; index++)
            {
                resolver.ThrowIfCancellationRequested();
                var path = jv_array_get(jv_copy(searchPath), index);
                if (path.Kind != jv_kind.JV_KIND_STRING)
                {
                    jv_free(path);
                    continue;
                }

                path = expand_path(path, resolver);
                if (!path.IsValid)
                {
                    jv_free(error);
                    error = path;
                    path = jv_null();
                    continue;
                }

                jv expandedValue;
                if (path.StringValue.Equals(".", StringComparison.Ordinal))
                {
                    expandedValue = jv_copy(path);
                }
                else if (path.StringValue.StartsWith("$ORIGIN/", StringComparison.Ordinal))
                {
                    var origin = jqOrigin.Kind == jv_kind.JV_KIND_STRING
                        ? jqOrigin.StringValue
                        : resolver.JqOrigin;
                    expandedValue = jv_string(CombineSearchPath(
                        origin,
                        path.StringValue["$ORIGIN/".Length..]));
                }
                else if (libraryOrigin.Kind == jv_kind.JV_KIND_STRING &&
                         path_is_relative(jv_copy(path)) != 0)
                {
                    expandedValue = jv_string(CombineSearchPath(
                        libraryOrigin.StringValue,
                        path.StringValue));
                }
                else
                {
                    expandedValue = path;
                    path = jv_invalid();
                }

                expanded = jv_array_append(expanded, expandedValue);
                jv_free(path);
            }

            var result = jv_array_append(jv_array(), expanded);
            expanded = jv_invalid();
            result = jv_array_append(result, error);
            error = jv_invalid();
            return result;
        }
        finally
        {
            jv_free(expanded);
            jv_free(error);
            jv_free(searchPath);
            jv_free(jqOrigin);
            jv_free(libraryOrigin);
        }
    }

    // jq-1.8.2 src/linker.c:find_lib(). The explicit filesystem capability
    // replaces stat/jv_load_file probing; every input jv is still consumed.
    internal static jv find_lib(
        JqModuleResolver resolver,
        jv relativePath,
        jv search,
        string suffix,
        jv jqOrigin,
        jv libraryOrigin) =>
        find_lib(
            resolver,
            relativePath,
            search,
            suffix,
            jqOrigin,
            libraryOrigin,
            out _);

    // The managed public adapter needs the exact capability-read snapshot that
    // made stat-like probing succeed. Keeping it as an out value on this same
    // source-shaped search prevents a second, potentially different read.
    private static jv find_lib(
        JqModuleResolver resolver,
        jv relativePath,
        jv search,
        string suffix,
        jv jqOrigin,
        jv libraryOrigin,
        out JqFileReadResult? selectedRead)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(suffix);
        selectedRead = null;
        if (!relativePath.IsValid)
        {
            jv_free(search);
            jv_free(jqOrigin);
            jv_free(libraryOrigin);
            return relativePath;
        }

        if (relativePath.Kind != jv_kind.JV_KIND_STRING)
        {
            jv_free(relativePath);
            jv_free(search);
            jv_free(jqOrigin);
            jv_free(libraryOrigin);
            return jv_invalid_with_msg(jv_string("Module path must be a string"));
        }

        if (search.Kind != jv_kind.JV_KIND_ARRAY)
        {
            jv_free(relativePath);
            jv_free(search);
            jv_free(jqOrigin);
            jv_free(libraryOrigin);
            return jv_invalid_with_msg(jv_string("Module search path must be an array"));
        }

        var relPathValue = relativePath.StringValue;
        var chain = build_lib_search_chain(
            resolver,
            search,
            jqOrigin,
            libraryOrigin);
        var expansionError = jv_array_get(jv_copy(chain), 1);
        var paths = jv_array_get(chain, 0);
        chain = jv_invalid();
        var basename = jv_basename(jv_copy(relativePath));
        try
        {
            var pathCount = jv_array_length(jv_copy(paths));
            for (var pathIndex = 0; pathIndex < pathCount; pathIndex++)
            {
                resolver.ThrowIfCancellationRequested();
                var searchPath = jv_array_get(jv_copy(paths), pathIndex);
                try
                {
                    if (searchPath.Kind == jv_kind.JV_KIND_NULL)
                    {
                        break;
                    }

                    if (searchPath.Kind != jv_kind.JV_KIND_STRING || searchPath.StringValue.Length == 0)
                    {
                        continue;
                    }

                    var read = probe_lib_search_root(
                        resolver,
                        build_lib_candidates(
                            searchPath.StringValue,
                            relPathValue,
                            suffix,
                            basename.StringValue));
                    if (read is { } candidate)
                    {
                        // stat(2) succeeds for directories and for regular files
                        // that the host refuses to read only because of its size
                        // policy. Return that existing layout and let jv_load_file
                        // produce the source-shaped open/load error.
                        selectedRead = candidate;
                        jv_free(relativePath);
                        relativePath = jv_invalid();
                        return jv_string(candidate.Path);
                    }
                }
                finally
                {
                    jv_free(searchPath);
                }
            }

            string message;
            if (!expansionError.IsValid)
            {
                var detail = jv_invalid_get_msg(expansionError);
                expansionError = jv_invalid();
                try
                {
                    message = $"module not found: {relPathValue} ({detail.StringValue})";
                }
                finally
                {
                    jv_free(detail);
                }
            }
            else
            {
                message = "module not found: " + relPathValue;
            }

            jv_free(relativePath);
            relativePath = jv_invalid();
            return jv_invalid_with_msg(jv_string(message));
        }
        finally
        {
            jv_free(relativePath);
            jv_free(chain);
            jv_free(paths);
            jv_free(expansionError);
            jv_free(basename);
        }
    }
}
// jq-1.8.2 src/linker.c:lib_entry/lib_loading_state. The instruction block
// remains an owning-by-convention value exactly like native; the managed state
// adds only explicit filesystem graph accounting required by JqModuleResolver.
internal sealed class lib_entry(string name, block def, int loading)
{
    internal string name = name;

    internal block def = def;

    internal int loading = loading;
}

internal sealed class lib_loading_state(JqModuleResolver resolver)
{
    private readonly HashSet<string> accountedFiles = new(StringComparer.Ordinal);
    private long totalImportedBytes;

    internal List<lib_entry> entries { get; } = [];

    internal ulong ct => (ulong)entries.Count;

    internal void CheckDependencyDepth(int depth)
    {
        resolver.ThrowIfCancellationRequested();
        if (resolver.Limits.MaxDependencyDepth is { } maximum && depth > maximum)
        {
            throw new JqCompileException(
                $"jq: error: module dependency-depth limit exceeded (maximum {maximum})");
        }
    }

    internal void Account(string path, long byteCount)
    {
        resolver.ThrowIfCancellationRequested();
        if (accountedFiles.Contains(path))
        {
            return;
        }

        if (resolver.Limits.MaxModuleCount is { } maximumCount &&
            accountedFiles.Count >= maximumCount)
        {
            throw new JqCompileException(
                $"jq: error: module-count limit exceeded (maximum {maximumCount})");
        }

        if (resolver.Limits.MaxTotalImportedBytes is { } maximumBytes &&
            (byteCount > maximumBytes || totalImportedBytes > maximumBytes - byteCount))
        {
            throw new JqCompileException(
                $"jq: error: total imported-byte limit exceeded (maximum {maximumBytes})");
        }

        accountedFiles.Add(path);
        totalImportedBytes += byteCount;
    }
}

internal static partial class libjq
{
    // jq-1.8.2 src/linker.c:process_dependencies(). JqModuleResolver is the
    // deliberate managed capability parameter replacing jq_state's ambient
    // stat/open access; dependency order, block moves, and bindings follow C.
    internal static int process_dependencies(
        jq_state jq,
        JqModuleResolver resolver,
        jv jqOrigin,
        jv libraryOrigin,
        ref block srcBlock,
        lib_loading_state libState,
        int dependencyDepth)
    {
        ArgumentNullException.ThrowIfNull(jq);
        ArgumentNullException.ThrowIfNull(resolver);
        ArgumentNullException.ThrowIfNull(libState);
        var deps = block_take_imports(ref srcBlock);
        var errors = 0;
        try
        {
            var dependencyCount = jv_array_length(jv_copy(deps));
            // Backward iteration is source-significant: later bindings must be
            // installed first so earlier source declarations shadow correctly.
            for (var index = dependencyCount - 1; index >= 0; index--)
            {
                resolver.ThrowIfCancellationRequested();
                try
                {
                    libState.CheckDependencyDepth(dependencyDepth);
                }
                catch (JqCompileException exception)
                {
                    report_linker_error(jq, exception.Message);
                    return 1;
                }

                var dependency = jv_array_get(jv_copy(deps), index);
                var alias = jv_invalid();
                try
                {
                    var isDataValue = jv_object_get(
                        jv_copy(dependency),
                        jv_string("is_data"));
                    var rawValue = jv_object_get(
                        jv_copy(dependency),
                        jv_string("raw"));
                    var optionalValue = jv_object_get(
                        jv_copy(dependency),
                        jv_string("optional"));
                    var isData = isDataValue.Kind == jv_kind.JV_KIND_TRUE;
                    var raw = rawValue.Kind == jv_kind.JV_KIND_TRUE;
                    var optional = optionalValue.Kind == jv_kind.JV_KIND_TRUE;
                    jv_free(isDataValue);
                    jv_free(rawValue);
                    jv_free(optionalValue);

                    var relativePath = validate_relpath(
                        jv_object_get(jv_copy(dependency), jv_string("relpath")));
                    alias = jv_object_get(
                        jv_copy(dependency),
                        jv_string("as"));
                    var aliasText = alias.Kind == jv_kind.JV_KIND_STRING
                        ? alias.StringValue
                        : null;
                    var search = default_search(
                        resolver,
                        jv_object_get(dependency, jv_string("search")));
                    dependency = jv_invalid();
                    var resolved = find_lib(
                        resolver,
                        relativePath,
                        search,
                        isData ? ".json" : ".jq",
                        jv_copy(jqOrigin),
                        jv_copy(libraryOrigin));
                    if (!resolved.IsValid)
                    {
                        if (optional)
                        {
                            jv_free(resolved);
                            continue;
                        }

                        var error = jv_invalid_has_msg(jv_copy(resolved))
                            ? jv_invalid_get_msg(resolved)
                            : jv_string("unknown error");
                        resolved = jv_invalid();
                        try
                        {
                            report_linker_error(jq, "jq: error: " + error.StringValue + "\n");
                        }
                        finally
                        {
                            jv_free(error);
                        }

                        return 1;
                    }

                    if (isData)
                    {
                        errors += load_library(
                            jq,
                            resolver,
                            resolved,
                            isData: true,
                            raw,
                            optional,
                            aliasText,
                            out var dependencyDefinition,
                            libState,
                            dependencyDepth);
                        if (errors == 0)
                        {
                            srcBlock = block_bind_library(
                                dependencyDefinition,
                                srcBlock,
                                OP_IS_CALL_PSEUDO,
                                aliasText);
                            srcBlock = block_bind_library(
                                dependencyDefinition,
                                srcBlock,
                                OP_IS_CALL_PSEUDO,
                                null);
                        }
                        else if (!ContainsOwnedBlock(libState, dependencyDefinition))
                        {
                            block_free(dependencyDefinition);
                        }

                        continue;
                    }

                    var resolvedPath = resolved.StringValue;
                    var stateIndex = 0;
                    while (stateIndex < libState.entries.Count &&
                           !libState.entries[stateIndex].name.Equals(
                               resolvedPath,
                               StringComparison.Ordinal))
                    {
                        stateIndex++;
                    }

                    if (stateIndex < libState.entries.Count)
                    {
                        var entry = libState.entries[stateIndex];
                        jv_free(resolved);
                        if (entry.loading != 0)
                        {
                            report_linker_error(
                                jq,
                                "jq: error: circular import of " + resolvedPath + "\n");
                            return 1;
                        }

                        srcBlock = block_bind_library(
                            entry.def,
                            srcBlock,
                            OP_IS_CALL_PSEUDO,
                            aliasText);
                    }
                    else
                    {
                        errors += load_library(
                            jq,
                            resolver,
                            resolved,
                            isData: false,
                            raw,
                            optional,
                            aliasText,
                            out var dependencyDefinition,
                            libState,
                            dependencyDepth);
                        if (errors == 0)
                        {
                            srcBlock = block_bind_library(
                                dependencyDefinition,
                                srcBlock,
                                OP_IS_CALL_PSEUDO,
                                aliasText);
                        }
                        else if (!ContainsOwnedBlock(libState, dependencyDefinition))
                        {
                            // Native leaks the rejected library-main block in this
                            // path; managed ownership closes it deterministically.
                            block_free(dependencyDefinition);
                        }
                    }
                }
                finally
                {
                    jv_free(dependency);
                    jv_free(alias);
                }
            }

            return errors;
        }
        finally
        {
            jv_free(libraryOrigin);
            jv_free(jqOrigin);
            jv_free(deps);
        }
    }

    // jq-1.8.2 src/linker.c:load_library(). The block returned through
    // outBlock remains owned by libState for a registered library and otherwise
    // by the caller. libPath is consumed on every path.
    private static int load_library(
        jq_state jq,
        JqModuleResolver resolver,
        jv libPath,
        bool isData,
        bool raw,
        bool optional,
        string? alias,
        out block outBlock,
        lib_loading_state libState,
        int dependencyDepth)
    {
        var errors = 0;
        var program = gen_noop();
        var data = jv_invalid();
        locfile? sourceFile = null;
        var path = libPath.Kind == jv_kind.JV_KIND_STRING
            ? libPath.StringValue
            : string.Empty;
        try
        {
            resolver.ThrowIfCancellationRequested();
            var read = resolver.FileSystem.ReadFile(path);
            resolver.ThrowIfCancellationRequested();
            if (!read.IsSuccess)
            {
                var detail = read.Status == JqFileReadStatus.IsDirectory
                    ? "It's a directory"
                    : read.ErrorMessage ?? "unknown error";
                data = jv_invalid_with_msg(jv_string($"Could not open {path}: {detail}"));
            }
            else
            {
                libState.Account(path, read.Contents.Length);
                data = jv_load_file(new PreloadedModuleFileSystem(path, read), path, raw || !isData ? 1 : 0);
            }

            if (!data.IsValid)
            {
                if (!optional)
                {
                    var message = jv_invalid_has_msg(jv_copy(data))
                        ? jv_invalid_get_msg(data)
                        : jv_string("unknown error");
                    data = jv_invalid();
                    try
                    {
                        report_linker_error(
                            jq,
                            $"jq: error loading data file {path}: {message.StringValue}\n");
                    }
                    finally
                    {
                        jv_free(message);
                    }

                    errors++;
                }

                outBlock = program;
                program = gen_noop();
                return errors;
            }

            if (isData)
            {
                if (alias is null)
                {
                    report_linker_error(jq, "jq: error: data import requires an alias");
                    errors++;
                }
                else
                {
                    program = gen_const_global(jv_copy(data), alias);
                    libState.entries.Add(new lib_entry(path, program, loading: 0));
                }

                outBlock = program;
                program = gen_noop();
                return errors;
            }

            var sourceBytes = data.Kind == jv_kind.JV_KIND_STRING
                ? jvp_string_data(data).ToArray()
                : Array.Empty<byte>();
            sourceFile = locfile_init(jq, path, sourceBytes, sourceBytes.Length);
            errors += jq_parse_library(sourceFile, out program);
            if (errors == 0)
            {
                // Register before recursive dependency loading so cycles see loading=1.
                var entry = new lib_entry(path, gen_noop(), loading: 1);
                libState.entries.Add(entry);
                errors += process_dependencies(
                    jq,
                    resolver,
                    jv_string(resolver.JqOrigin),
                    jv_string(DirectoryNameForLinker(path)),
                    ref program,
                    libState,
                    checked(dependencyDepth + 1));
                program = block_bind_self(program, OP_IS_CALL_PSEUDO);
                entry.def = program;
                entry.loading = 0;
            }

            outBlock = program;
            program = gen_noop();
            return errors;
        }
        catch (JqCompileException exception)
        {
            report_linker_error(jq, exception.Message);
            outBlock = program;
            program = gen_noop();
            return errors + 1;
        }
        finally
        {
            if (sourceFile is not null)
            {
                locfile_free(sourceFile);
            }

            block_free(program);
            jv_free(libPath);
            jv_free(data);
        }
    }

    // jq-1.8.2 src/linker.c:load_module_meta(). This direct-IR overload
    // returns module metadata/dependencies/definitions from the parsed block.
    internal static jv load_module_meta(jq_state jq, jv moduleRelativePath)
    {
        ArgumentNullException.ThrowIfNull(jq);
        var resolver = jq.ModuleResolver;
        if (resolver is null)
        {
            jv_free(moduleRelativePath);
            return jv_invalid_with_msg(jv_string(
                "modulemeta resolver is unavailable at the direct C-function boundary"));
        }

        try
        {
            return load_module_meta(jq, resolver, moduleRelativePath);
        }
        catch (JqRuntimeException exception)
        {
            var error = exception.TakeErrorValue() ?? jv_string(exception.Message);
            return jv_invalid_with_msg(error);
        }
        catch (JqCompileException exception)
        {
            // modulemeta is a runtime builtin even though it reuses the parser
            // and graph-accounting machinery. Convert those loader failures to
            // jq's invalid-with-message result at this C-function boundary.
            return jv_invalid_with_msg(jv_string(exception.Message));
        }
    }

    internal static jv load_module_meta(
        jq_state jq,
        JqModuleResolver resolver,
        jv moduleRelativePath)
    {
        ArgumentNullException.ThrowIfNull(jq);
        ArgumentNullException.ThrowIfNull(resolver);
        var search = jv_array();
        try
        {
            foreach (var path in resolver.LibraryPaths)
            {
                resolver.ThrowIfCancellationRequested();
                search = jv_array_append(search, jv_string(path));
            }
        }
        catch
        {
            jv_free(search);
            jv_free(moduleRelativePath);
            throw;
        }

        var libraryPath = find_lib(
            resolver,
            validate_relpath(moduleRelativePath),
            search,
            ".jq",
            jv_string(resolver.JqOrigin),
            jv_null());
        if (!libraryPath.IsValid)
        {
            return libraryPath;
        }

        var pathValue = libraryPath.StringValue;
        var metadata = jv_null();
        var data = jv_invalid();
        var program = gen_noop();
        locfile? sourceFile = null;
        try
        {
            resolver.ThrowIfCancellationRequested();
            var read = resolver.FileSystem.ReadFile(pathValue);
            resolver.ThrowIfCancellationRequested();
            if (!read.IsSuccess)
            {
                return metadata;
            }

            var state = new lib_loading_state(resolver);
            state.CheckDependencyDepth(1);
            state.Account(pathValue, read.Contents.Length);
            data = jv_load_file(
                new PreloadedModuleFileSystem(pathValue, read),
                pathValue,
                raw: 1);
            if (!data.IsValid)
            {
                return metadata;
            }

            var sourceBytes = jvp_string_data(data).ToArray();
            sourceFile = locfile_init(jq, pathValue, sourceBytes, sourceBytes.Length);
            var errors = jq_parse_library(sourceFile, out program);
            if (errors == 0)
            {
                jv_free(metadata);
                metadata = block_module_meta(program);
                if (metadata.Kind == jv_kind.JV_KIND_NULL)
                {
                    jv_free(metadata);
                    metadata = jv_object();
                }

                metadata = jv_object_set(
                    metadata,
                    "deps",
                    block_take_imports(ref program));
                metadata = jv_object_set(
                    metadata,
                    "defs",
                    block_list_funcs(program, omitUnderscores: 0));
            }

            return metadata;
        }
        catch
        {
            jv_free(metadata);
            throw;
        }
        finally
        {
            if (sourceFile is not null)
            {
                locfile_free(sourceFile);
            }

            block_free(program);
            jv_free(data);
            jv_free(libraryPath);
        }
    }

    // jq-1.8.2 src/linker.c:load_program(). The explicit resolver and startup
    // opt-in are the managed host boundary; parsing/linking/cleanup stays native-shaped.
    internal static int load_program(
        jq_state jq,
        locfile source,
        JqModuleResolver resolver,
        out block outBlock,
        string? programOrigin = null,
        bool loadUserStartupLibrary = false)
    {
        ArgumentNullException.ThrowIfNull(jq);
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(resolver);
        jq_set_module_resolver(jq, resolver);
        outBlock = gen_noop();
        var program = gen_noop();
        var libState = new lib_loading_state(resolver);
        var errors = jq_parse(source, out program);
        if (errors != 0)
        {
            block_free(program);
            return errors;
        }

        if (!block_has_main(program))
        {
            report_linker_error(jq, "jq: error: Top-level program not given (try \".\")");
            block_free(program);
            return 1;
        }

        if (loadUserStartupLibrary && resolver.HomeDirectory is { } homeDirectory)
        {
            var import = gen_import(jv_string(string.Empty), jv_invalid(), isData: 0);
            var metadata = jv_object_set(
                jv_object_set(jv_object(), "optional", jv_true()),
                "search",
                jv_string(homeDirectory));
            program = BLOCK(gen_import_meta(import, gen_const(metadata)), program);
        }

        var libraries = gen_noop();
        try
        {
            errors = process_dependencies(
                jq,
                resolver,
                jv_string(resolver.JqOrigin),
                programOrigin is null ? jv_null() : jv_string(programOrigin),
                ref program,
                libState,
                dependencyDepth: 1);
            // Libraries are registered before their dependencies are loaded.
            // Materialize the owning block dependency-first so the one-pass
            // reverse reference marker reaches transitive module calls before
            // block_drop_unreferenced decides which binders to release.
            for (var index = libState.entries.Count - 1; index >= 0; index--)
            {
                var entry = libState.entries[index];
                if (errors == 0 && !block_is_const(entry.def))
                {
                    libraries = block_join(libraries, entry.def);
                }
                else
                {
                    block_free(entry.def);
                }

                entry.def = gen_noop();
            }

            if (errors != 0)
            {
                block_free(libraries);
                libraries = gen_noop();
                block_free(program);
                program = gen_noop();
            }
            else
            {
                outBlock = block_drop_unreferenced(block_join(libraries, program));
                libraries = gen_noop();
                program = gen_noop();
            }

            return errors;
        }
        catch (JqCompileException exception)
        {
            report_linker_error(jq, exception.Message);
            return 1;
        }
        finally
        {
            block_free(libraries);
            block_free(program);
            foreach (var entry in libState.entries)
            {
                block_free(entry.def);
                entry.def = gen_noop();
            }
        }
    }

    private static bool ContainsOwnedBlock(lib_loading_state state, block candidate) =>
        candidate.first is not null && state.entries.Any(entry =>
            ReferenceEquals(entry.def.first, candidate.first));

    private static string DirectoryNameForLinker(string path)
    {
        var separator = Math.Max(path.LastIndexOf('/'), path.LastIndexOf('\\'));
        return separator < 0 ? "." : path[..separator];
    }

    private static void report_linker_error(jq_state jq, string message)
        => jq_report_error(jq, jv_string(message));

    private sealed class PreloadedModuleFileSystem(
        string expectedPath,
        JqFileReadResult result) : IJqFileSystem
    {
        public JqFileReadResult ReadFile(string path) =>
            path.Equals(expectedPath, StringComparison.Ordinal)
                ? result
                : JqFileReadResult.Failure(JqFileReadStatus.NotFound, path, "missing");
    }
}
