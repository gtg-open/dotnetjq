// DOTNETJQ PORT MAP
// Upstream repository: https://github.com/jqlang/jq
// Upstream revision: 34f7186b86743a083a589741b6cea95293524108 (jq-1.8.2)
// Upstream file: src/jv_aux.c
// Upstream URL: https://github.com/jqlang/jq/blob/jq-1.8.2/src/jv_aux.c
// Strategy: PORT
// Target file: src/DotNetJq/Port/src/jv_aux.c.cs
// Substitutions: explicit managed frame stacks replace recursive C frames at jq's fixed
// 10,000-depth boundaries; jv_copy/jv_free ownership transfer remains source-visible.
// Known differences: CLR exceptions carry managed runtime errors across VM/public boundaries.

namespace DotNetJq.Port;

internal static partial class libjq
{
    // jq-1.8.2 src/jv.c: MAX_CONTAINS_DEPTH.
    private const int MaxContainsDepth = 10_000;

    // jq-1.8.2 src/jv_aux.c: MAX_CMP_DEPTH.
    private const int MaxComparisonDepth = 10_000;

    // jq-1.8.2 src/jv_aux.c: MAX_PATH_DEPTH.
    private const int MaxPathDepth = 10_000;

    internal static int jv_cmp(jv left, jv right)
    {
        var pending = new Stack<ComparisonWork>();
        pending.Push(ComparisonWork.Values(left, right, 0));
        try
        {
            while (pending.Count > 0)
            {
                var current = pending.Pop();
                if (current.Kind == ComparisonWorkKind.Result)
                {
                    if (current.Result != 0)
                    {
                        return current.Result;
                    }

                    continue;
                }

                if (current.Depth > MaxComparisonDepth)
                {
                    throw new JqRuntimeException("Comparison too deep");
                }

                if (current.Left.Kind != current.Right.Kind)
                {
                    return current.Left.Kind.CompareTo(current.Right.Kind);
                }

                switch (current.Left.Kind)
                {
                    case jv_kind.JV_KIND_INVALID:
                    case jv_kind.JV_KIND_NULL:
                    case jv_kind.JV_KIND_FALSE:
                    case jv_kind.JV_KIND_TRUE:
                        break;
                    case jv_kind.JV_KIND_NUMBER:
                    {
                        var comparison = CompareNumbers(current.Left, current.Right);
                        if (comparison != 0)
                        {
                            return comparison;
                        }

                        break;
                    }
                    case jv_kind.JV_KIND_STRING:
                    {
                        var comparison = jvp_string_cmp(current.Left, current.Right);
                        if (comparison != 0)
                        {
                            return comparison;
                        }

                        break;
                    }
                    case jv_kind.JV_KIND_ARRAY:
                        QueueArrayComparison(
                            pending,
                            current.Left.ArrayValue,
                            current.Right.ArrayValue,
                            current.Depth);
                        break;
                    case jv_kind.JV_KIND_OBJECT:
                        QueueObjectComparison(pending, current.Left, current.Right, current.Depth);
                        break;
                }
            }

            return 0;
        }
        finally
        {
            // jq-1.8.2 src/jv_aux.c:jvp_cmp() consumes both operands on all
            // exits, including its INT_MIN/depth-error result.  Pending child
            // comparisons borrow storage owned by these roots.
            jv_free(left);
            jv_free(right);
        }
    }

    private static void QueueArrayComparison(
        Stack<ComparisonWork> pending,
        IReadOnlyList<jv> left,
        IReadOnlyList<jv> right,
        int depth)
    {
        pending.Push(ComparisonWork.Fixed(left.Count.CompareTo(right.Count)));
        var length = Math.Min(left.Count, right.Count);
        for (var index = length - 1; index >= 0; index--)
        {
            pending.Push(ComparisonWork.Values(left[index], right[index], depth + 1));
        }
    }

    private static void QueueObjectComparison(
        Stack<ComparisonWork> pending,
        jv left,
        jv right,
        int depth)
    {
        // Upstream compares the sorted key arrays first, then the values.  The
        // key array comparison itself is at depth + 1 even for an empty object.
        // Checking it explicitly preserves jq's slightly earlier object limit.
        if (depth + 1 > MaxComparisonDepth)
        {
            throw new JqRuntimeException("Comparison too deep");
        }

        var leftOrdered = jvp_object_ptr(left).Elements
            .Where(static slot => slot.String.Kind != jv_kind.JV_KIND_NULL)
            .ToArray();
        var rightOrdered = jvp_object_ptr(right).Elements
            .Where(static slot => slot.String.Kind != jv_kind.JV_KIND_NULL)
            .ToArray();
        Array.Sort(leftOrdered, static (first, second) =>
            jvp_string_cmp(first.String, second.String));
        Array.Sort(rightOrdered, static (first, second) =>
            jvp_string_cmp(first.String, second.String));
        var length = Math.Min(leftOrdered.Length, rightOrdered.Length);

        // These run only after every key comparison has produced equality.
        for (var index = length - 1; index >= 0; index--)
        {
            pending.Push(ComparisonWork.Values(
                leftOrdered[index].Value,
                rightOrdered[index].Value,
                depth + 1));
        }

        pending.Push(ComparisonWork.Fixed(leftOrdered.Length.CompareTo(rightOrdered.Length)));
        for (var index = length - 1; index >= 0; index--)
        {
            pending.Push(ComparisonWork.Values(
                leftOrdered[index].String,
                rightOrdered[index].String,
                depth + 2));
        }
    }

    private enum ComparisonWorkKind
    {
        Values,
        Result,
    }

    private readonly record struct ComparisonWork(
        ComparisonWorkKind Kind,
        jv Left,
        jv Right,
        int Depth,
        int Result)
    {
        internal static ComparisonWork Values(jv left, jv right, int depth) =>
            new(ComparisonWorkKind.Values, left, right, depth, 0);

        internal static ComparisonWork Fixed(int result) =>
            new(ComparisonWorkKind.Result, default, default, 0, result);
    }

    internal static bool jv_contains(jv container, jv contained)
    {
        var work = new Stack<ContainsFrame>();
        work.Push(ContainsFrame.CreateOwned(container, contained, 0));
        try
        {
            while (work.Count > 0)
            {
                var current = work.Peek();
                if (current.IsComplete)
                {
                    var result = current.Result;
                    work.Pop();
                    current.Release();
                    if (work.Count == 0)
                    {
                        return result;
                    }

                    work.Peek().AcceptChild(result);
                    continue;
                }

                work.Push(current.CreateNextChild());
            }

            return false;
        }
        finally
        {
            // Every frame owns exactly the pair that native jvp_contains()
            // would receive.  Drain on depth failure and other exceptions.
            while (work.TryPop(out var frame))
            {
                frame.Release();
            }
        }
    }

    private sealed class ContainsFrame
    {
        private readonly jv container;
        private readonly jv contained;
        private int containedIndex;
        private int candidateIndex;
        private bool awaitingChild;
        private readonly int depth;
        private bool released;

        private ContainsFrame(jv container, jv contained, int depth)
        {
            this.container = container;
            this.contained = contained;
            this.depth = depth;
            if (container.Kind != contained.Kind)
            {
                Complete(false);
                return;
            }

            switch (container.Kind)
            {
                case jv_kind.JV_KIND_STRING:
                    Complete(_jq_memmem(
                        jvp_string_data(container),
                        jvp_string_data(contained)) >= 0);
                    break;
                case jv_kind.JV_KIND_ARRAY:
                    if (contained.ArrayValue.Count == 0)
                    {
                        Complete(true);
                    }
                    else if (container.ArrayValue.Count == 0)
                    {
                        Complete(false);
                    }

                    break;
                case jv_kind.JV_KIND_OBJECT:
                    if (jvp_object_length(contained) == 0)
                    {
                        Complete(true);
                    }

                    break;
                default:
                    // jv_equal() consumes its operands, while this frame must
                    // retain its owners until it completes.
                    Complete(jv_equal(jv_copy(container), jv_copy(contained)));
                    break;
            }
        }

        internal static ContainsFrame CreateOwned(jv container, jv contained, int depth)
        {
            if (depth > MaxContainsDepth)
            {
                jv_free(container);
                jv_free(contained);
                throw new JqRuntimeException("Containment check too deep");
            }

            try
            {
                return new ContainsFrame(container, contained, depth);
            }
            catch
            {
                jv_free(container);
                jv_free(contained);
                throw;
            }
        }

        internal bool IsComplete { get; private set; }

        internal bool Result { get; private set; }

        internal ContainsFrame CreateNextChild()
        {
            if (IsComplete || awaitingChild)
            {
                throw new InvalidOperationException("Invalid contains traversal state");
            }

            jv childContainer;
            jv childContained;
            if (container.Kind == jv_kind.JV_KIND_ARRAY)
            {
                // Native jvp_array_contains() obtains owning children through
                // jv_array_get(jv_copy(...)); reproduce those transfers.
                childContainer = jv_array_get(jv_copy(container), candidateIndex);
                childContained = jv_array_get(jv_copy(contained), containedIndex);
            }
            else
            {
                var wanted = GetObjectSlot(contained, containedIndex);
                // jq-1.8.2 src/jv.c:jvp_object_contains(): object_get owns
                // the found value (or invalid) and the iterator owns b_val.
                childContainer = jv_object_get(jv_copy(container), jv_copy(wanted.String));
                childContained = jv_copy(wanted.Value);
            }

            awaitingChild = true;
            return CreateOwned(childContainer, childContained, depth + 1);
        }

        internal void AcceptChild(bool childResult)
        {
            if (!awaitingChild)
            {
                throw new InvalidOperationException("Contains traversal has no pending child");
            }

            awaitingChild = false;
            if (container.Kind == jv_kind.JV_KIND_OBJECT)
            {
                if (!childResult)
                {
                    Complete(false);
                    return;
                }

                containedIndex++;
                if (containedIndex == jvp_object_length(contained))
                {
                    Complete(true);
                }

                return;
            }

            if (childResult)
            {
                containedIndex++;
                candidateIndex = 0;
                if (containedIndex == contained.ArrayValue.Count)
                {
                    Complete(true);
                }

                return;
            }

            candidateIndex++;
            if (candidateIndex == container.ArrayValue.Count)
            {
                Complete(false);
            }
        }

        private void Complete(bool result)
        {
            IsComplete = true;
            Result = result;
        }

        private static object_slot GetObjectSlot(jv value, int liveIndex)
        {
            foreach (var slot in jvp_object_ptr(value).Elements)
            {
                if (slot.String.Kind == jv_kind.JV_KIND_NULL)
                {
                    continue;
                }

                if (liveIndex-- == 0)
                {
                    return slot;
                }
            }

            throw new ArgumentOutOfRangeException(nameof(liveIndex));
        }

        internal void Release()
        {
            if (released)
            {
                return;
            }

            released = true;
            jv_free(container);
            jv_free(contained);
        }
    }

    internal static jv jv_getpath(jv root, IReadOnlyList<jv> path)
    {
        var current = root;
        foreach (var component in path)
        {
            current = jv_get(current, jv_copy(component));
            if (!current.IsValid)
            {
                break;
            }
        }

        return current;
    }

    internal static jv jv_getpath(jv root, jv path)
    {
        if (path.Kind != jv_kind.JV_KIND_ARRAY)
        {
            jv_free(root);
            jv_free(path);
            return jv_invalid_with_msg(jv_string("Path must be specified as an array"));
        }

        var pathLength = jv_array_length(jv_copy(path));
        if (pathLength > 10_000)
        {
            jv_free(root);
            jv_free(path);
            return jv_invalid_with_msg(jv_string("Path too deep"));
        }

        // jq propagates an invalid root before considering an empty or
        // non-empty path; preserve its payload/allocation identity.
        if (!root.IsValid)
        {
            jv_free(path);
            return root;
        }

        for (var index = 0; index < pathLength; index++)
        {
            root = jv_get(root, jv_array_get(jv_copy(path), index));
            if (!root.IsValid)
            {
                break;
            }
        }

        jv_free(path);
        return root;
    }

    internal static jv jv_setpath(jv root, IReadOnlyList<jv> path, jv value)
    {
        try
        {
            return PathUpdates.SetAtPathOwned(root, path, value);
        }
        catch (JqRuntimeException exception)
        {
            try
            {
                return jv_invalid_with_msg(jv_string(exception.Message));
            }
            finally
            {
                exception.ReleaseErrorValue();
            }
        }
    }

    internal static jv jv_setpath(jv root, jv path, jv value)
    {
        if (path.Kind != jv_kind.JV_KIND_ARRAY)
        {
            jv_free(value);
            jv_free(root);
            jv_free(path);
            return jv_invalid_with_msg(jv_string("Path must be specified as an array"));
        }

        var pathLength = jv_array_length(jv_copy(path));
        if (pathLength > 10_000)
        {
            jv_free(value);
            jv_free(root);
            jv_free(path);
            return jv_invalid_with_msg(jv_string("Path too deep"));
        }

        if (!root.IsValid)
        {
            jv_free(value);
            jv_free(path);
            return root;
        }

        try
        {
            var result = PathUpdates.SetAtPathOwned(root, path.ArrayValue, value);
            jv_free(path);
            return result;
        }
        catch (JqRuntimeException exception)
        {
            try
            {
                // SetAtPathOwned consumes root/value on every branch.  The
                // path array remains this wrapper's owner until conversion.
                jv_free(path);
                return jv_invalid_with_msg(jv_string(exception.Message));
            }
            finally
            {
                exception.ReleaseErrorValue();
            }
        }
    }

    internal static jv jv_delpath(jv root, IReadOnlyList<jv> path)
    {
        // Borrowing managed convenience wrapper: build the single owned path
        // expected by upstream jv_delpaths() and use the same iterative engine.
        var ownedPath = jv_array_sized(path.Count);
        foreach (var component in path)
        {
            ownedPath = jv_array_append(ownedPath, jv_copy(component));
        }

        return jv_delpaths(root, jv_array([ownedPath]));
    }

    internal static jv jv_get(jv target, jv key, bool optional)
    {
        var result = jv_get(target, key);
        if (result.IsValid || !jv_invalid_has_msg(jv_copy(result)))
        {
            return result;
        }

        if (optional)
        {
            jv_free(result);
            return jv_invalid();
        }

        throw new JqRuntimeException(jv_invalid_get_msg(result));
    }

    // jq-1.8.2 src/jv_aux.c: raw jv_get() consumes a numeric index by
    // truncating it toward zero; NaN and out-of-range reads become null.
    internal static jv jv_get(jv target, jv key)
    {
        try
        {
            if (target.Kind == jv_kind.JV_KIND_OBJECT && key.Kind == jv_kind.JV_KIND_STRING)
            {
                var result = jv_object_get(target, key);
                return result.IsValid ? result : jv_null();
            }

            if (target.Kind == jv_kind.JV_KIND_ARRAY && key.Kind == jv_kind.JV_KIND_NUMBER)
            {
                if (double.IsNaN(key.NumberValue))
                {
                    jv_free(target);
                    jv_free(key);
                    return jv_null();
                }

                var indexValue = Math.Clamp(key.NumberValue, int.MinValue, int.MaxValue);
                var index = (int)indexValue;
                if (index < 0)
                {
                    index += jv_array_length(jv_copy(target));
                }

                var result = jv_array_get(target, index);
                jv_free(key);
                if (!result.IsValid)
                {
                    jv_free(result);
                    return jv_null();
                }

                return result;
            }

            if ((target.Kind is jv_kind.JV_KIND_ARRAY or jv_kind.JV_KIND_STRING) &&
                key.Kind == jv_kind.JV_KIND_OBJECT)
            {
                var result = PathUpdates.GetAtPath(target, [key]);
                jv_free(target);
                jv_free(key);
                return result;
            }

            if (target.Kind == jv_kind.JV_KIND_ARRAY && key.Kind == jv_kind.JV_KIND_ARRAY)
            {
                return jv_array_indexes(target, key);
            }

            if (target.Kind == jv_kind.JV_KIND_NULL &&
                key.Kind is jv_kind.JV_KIND_STRING or jv_kind.JV_KIND_NUMBER or jv_kind.JV_KIND_OBJECT)
            {
                jv_free(target);
                jv_free(key);
                return jv_null();
            }

            var invalid = jv_invalid_with_msg(jv_string(
                "Cannot index " + jv_kind_name(target.Kind) + " with " +
                jv_kind_name(key.Kind) + " (" +
                jv_dump_string_trunc_borrowed(key, 30) + ")"));
            jv_free(target);
            jv_free(key);
            return invalid;
        }
        catch (JqRuntimeException exception)
        {
            try
            {
                return jv_invalid_with_msg(jv_string(exception.Message));
            }
            finally
            {
                exception.ReleaseErrorValue();
            }
        }
    }

    internal static jv jv_set(jv target, jv key, jv value)
    {
        if (!value.IsValid)
        {
            jv_free(target);
            jv_free(key);
            return value;
        }

        try
        {
            if (key.Kind == jv_kind.JV_KIND_STRING &&
                target.Kind is jv_kind.JV_KIND_OBJECT or jv_kind.JV_KIND_NULL)
            {
                if (target.Kind == jv_kind.JV_KIND_NULL)
                {
                    jv_free(target);
                    target = jv_object();
                }

                return jv_object_set(target, key, value);
            }

            if (key.Kind == jv_kind.JV_KIND_NUMBER &&
                target.Kind is jv_kind.JV_KIND_ARRAY or jv_kind.JV_KIND_NULL)
            {
                if (double.IsNaN(key.NumberValue))
                {
                    jv_free(target);
                    jv_free(key);
                    jv_free(value);
                    return jv_invalid_with_msg(jv_string("Cannot set array element at NaN index"));
                }

                var indexValue = Math.Clamp(key.NumberValue, int.MinValue, int.MaxValue);
                if (target.Kind == jv_kind.JV_KIND_NULL)
                {
                    jv_free(target);
                    target = jv_array();
                }

                target = jv_array_set(target, (int)indexValue, value);
                jv_free(key);
                return target;
            }

            if (key.Kind == jv_kind.JV_KIND_OBJECT &&
                target.Kind is jv_kind.JV_KIND_ARRAY or jv_kind.JV_KIND_NULL)
            {
                if (target.Kind == jv_kind.JV_KIND_NULL)
                {
                    jv_free(target);
                    target = jv_array();
                }

                try
                {
                    return PathUpdates.SetAtPathOwned(target, [key], value);
                }
                finally
                {
                    // jv_set() owns the slice key independently from the
                    // borrowed component view passed to the iterative helper.
                    jv_free(key);
                }
            }

            if (key.Kind == jv_kind.JV_KIND_OBJECT && target.Kind == jv_kind.JV_KIND_STRING)
            {
                jv_free(target);
                jv_free(key);
                jv_free(value);
                return jv_invalid_with_msg(jv_string("Cannot update string slices"));
            }

            var invalid = jv_invalid_with_msg(jv_string(
                "Cannot update field at " + jv_kind_name(key.Kind) +
                " index of " + jv_kind_name(target.Kind)));
            jv_free(target);
            jv_free(key);
            jv_free(value);
            return invalid;
        }
        catch (JqRuntimeException exception)
        {
            try
            {
                return jv_invalid_with_msg(jv_string(exception.Message));
            }
            finally
            {
                exception.ReleaseErrorValue();
            }
        }
    }

    internal static jv jv_has(jv target, jv key)
    {
        if (target.Kind == jv_kind.JV_KIND_NULL)
        {
            jv_free(target);
            jv_free(key);
            return jv_false();
        }

        if (target.Kind == jv_kind.JV_KIND_OBJECT && key.Kind == jv_kind.JV_KIND_STRING)
        {
            var element = jv_object_get(target, key);
            var exists = element.IsValid;
            jv_free(element);
            return jv_bool(exists);
        }

        if (target.Kind == jv_kind.JV_KIND_ARRAY && key.Kind == jv_kind.JV_KIND_NUMBER)
        {
            if (double.IsNaN(key.NumberValue))
            {
                jv_free(target);
                jv_free(key);
                return jv_false();
            }

            var clamped = Math.Clamp(key.NumberValue, int.MinValue, int.MaxValue);
            var index = (int)clamped;
            var element = jv_array_get(target, index);
            var exists = element.IsValid;
            jv_free(element);
            jv_free(key);
            return jv_bool(exists);
        }

        var invalid = jv_invalid_with_msg(jv_string(
            "Cannot check whether " + jv_kind_name(target.Kind) +
            " has a " + jv_kind_name(key.Kind) + " key"));
        jv_free(target);
        jv_free(key);
        return invalid;
    }

    internal static jv jv_delpaths(jv value, jv paths)
    {
        if (paths.Kind != jv_kind.JV_KIND_ARRAY)
        {
            // jq-1.8.2 src/jv_aux.c:jv_delpaths() consumes both arguments on
            // this error path before returning the diagnostic.
            jv_free(value);
            jv_free(paths);
            return jv_invalid_with_msg(jv_string("Paths must be specified as an array"));
        }

        // Native jq sorts the path array by a copy of itself before validating
        // or grouping it.  Keep that exact ownership edge: the sorted result
        // remains the sole `paths` owner consumed by delpaths_sorted().
        paths = jv_sort(paths, jv_copy(paths));
        if (!paths.IsValid)
        {
            jv_free(value);
            return paths;
        }

        var pathCount = jv_array_length(jv_copy(paths));
        for (var index = 0; index < pathCount; index++)
        {
            var path = jv_array_get(jv_copy(paths), index);
            if (path.Kind != jv_kind.JV_KIND_ARRAY)
            {
                var kind = path.Kind;
                jv_free(value);
                jv_free(paths);
                var error = jv_invalid_with_msg(jv_string(
                    "Path must be specified as array, not " + jv_kind_name(kind)));
                jv_free(path);
                return error;
            }

            if (jv_array_length(jv_copy(path)) > MaxPathDepth)
            {
                jv_free(value);
                jv_free(paths);
                jv_free(path);
                return jv_invalid_with_msg(jv_string("Path too deep"));
            }

            jv_free(path);
        }

        if (pathCount == 0)
        {
            jv_free(paths);
            return value;
        }

        var firstPathLength = jv_array_length(jv_array_get(jv_copy(paths), 0));
        if (firstPathLength == 0)
        {
            jv_free(paths);
            jv_free(value);
            return jv_null();
        }

        return delpaths_sorted(value, paths, 0);
    }

    // jq-1.8.2 src/jv_aux.c:delpaths_sorted().  Upstream uses one C frame per
    // path component.  A valid 10,000-component jq path exceeds the CLR call
    // stack, so the managed port stores the same locals and return continuation
    // explicitly.  Each frame owns exactly `object`, `paths`, and `delkeys`;
    // PendingKey is the native caller's live `key` while its child is running.
    private static jv delpaths_sorted(jv value, jv paths, int start)
    {
        var work = new Stack<DelpathsFrame>();
        work.Push(new DelpathsFrame(value, paths, start));
        try
        {
            while (work.Count > 0)
            {
                var frame = work.Peek();
                if (!frame.Object.IsValid ||
                    frame.Index >= jv_array_length(jv_copy(frame.Paths)))
                {
                    var completed = frame.Complete();
                    work.Pop();
                    if (work.Count == 0)
                    {
                        return completed;
                    }

                    var parent = work.Peek();
                    var pendingKey = parent.TakePendingKey();
                    var nextIndex = parent.PendingNextIndex;
                    if (!completed.IsValid)
                    {
                        jv_free(pendingKey);
                        jv_free(parent.Object);
                        parent.Object = completed;
                        parent.Index = jv_array_length(jv_copy(parent.Paths));
                    }
                    else
                    {
                        parent.Object = jv_set(parent.Object, pendingKey, completed);
                        parent.Index = nextIndex;
                    }

                    continue;
                }

                var index = frame.Index;
                // These separate array accesses intentionally mirror the three
                // consuming expressions at the top of upstream's group loop.
                var assertedLength = jv_array_length(
                    jv_array_get(jv_copy(frame.Paths), index));
                if (assertedLength <= frame.Start)
                {
                    throw new InvalidOperationException(
                        "delpaths_sorted received a path shorter than its prefix");
                }

                var deleteWholeKey = jv_array_length(
                    jv_array_get(jv_copy(frame.Paths), index)) == frame.Start + 1;
                var key = jv_array_get(
                    jv_array_get(jv_copy(frame.Paths), index),
                    frame.Start);
                var keyOwned = true;
                try
                {
                    var next = index;
                    do
                    {
                        next++;
                    }
                    while (next < jv_array_length(jv_copy(frame.Paths)) &&
                           jv_equal(
                               jv_copy(key),
                               jv_array_get(
                                   jv_array_get(jv_copy(frame.Paths), next),
                                   frame.Start)));

                    if (deleteWholeKey)
                    {
                        frame.DeleteKeys = jv_array_append(frame.DeleteKeys, key);
                        keyOwned = false;
                        frame.Index = next;
                        continue;
                    }

                    var subobject = jv_get(jv_copy(frame.Object), jv_copy(key));
                    if (!subobject.IsValid)
                    {
                        jv_free(key);
                        keyOwned = false;
                        jv_free(frame.Object);
                        frame.Object = subobject;
                        frame.Index = jv_array_length(jv_copy(frame.Paths));
                        continue;
                    }

                    if (subobject.Kind == jv_kind.JV_KIND_NULL)
                    {
                        jv_free(key);
                        keyOwned = false;
                        jv_free(subobject);
                        frame.Index = next;
                        continue;
                    }

                    frame.SetPendingKey(key, next);
                    keyOwned = false;
                    work.Push(new DelpathsFrame(
                        subobject,
                        jv_array_slice(jv_copy(frame.Paths), index, next),
                        frame.Start + 1));
                }
                finally
                {
                    if (keyOwned)
                    {
                        jv_free(key);
                    }
                }
            }

            throw new InvalidOperationException("delpaths_sorted exhausted without a result");
        }
        catch
        {
            while (work.TryPop(out var frame))
            {
                frame.Release();
            }

            throw;
        }
    }

    // jq-1.8.2 src/jv_aux.c:jv_dels(); consumes both arguments.  `keys` is
    // already sorted because delpaths_sorted() groups the jv_sort() result.
    private static jv jv_dels(jv target, jv keys)
    {
        if (target.Kind == jv_kind.JV_KIND_NULL ||
            jv_array_length(jv_copy(keys)) == 0)
        {
            jv_free(keys);
            return target;
        }

        if (target.Kind == jv_kind.JV_KIND_ARRAY)
        {
            var negativeKeys = jv_array();
            var nonnegativeKeys = jv_array();
            var result = jv_array();
            var starts = jv_array();
            var ends = jv_array();
            try
            {
                var keyCount = jv_array_length(jv_copy(keys));
                for (var index = 0; index < keyCount; index++)
                {
                    var key = jv_array_get(jv_copy(keys), index);
                    if (key.Kind == jv_kind.JV_KIND_NUMBER)
                    {
                        if (double.IsNaN(key.NumberValue))
                        {
                            jv_free(key);
                        }
                        else if (key.NumberValue < 0)
                        {
                            negativeKeys = jv_array_append(negativeKeys, key);
                        }
                        else
                        {
                            nonnegativeKeys = jv_array_append(nonnegativeKeys, key);
                        }

                        continue;
                    }

                    if (key.Kind == jv_kind.JV_KIND_OBJECT)
                    {
                        var parsed = parse_slice(
                            jv_copy(target),
                            key,
                            out var sliceStart,
                            out var sliceEnd);
                        if (parsed.Kind == jv_kind.JV_KIND_TRUE)
                        {
                            jv_free(parsed);
                            starts = jv_array_append(starts, jv_number(sliceStart));
                            ends = jv_array_append(ends, jv_number(sliceEnd));
                            continue;
                        }

                        jv_free(result);
                        result = parsed;
                        break;
                    }

                    var kind = key.Kind;
                    jv_free(result);
                    result = jv_invalid_with_msg(jv_string(
                        "Cannot delete " + jv_kind_name(kind) + " element of array"));
                    jv_free(key);
                    break;
                }

                if (result.IsValid)
                {
                    var negativeIndex = 0;
                    var nonnegativeIndex = 0;
                    var length = jv_array_length(jv_copy(target));
                    for (var index = 0; index < length; index++)
                    {
                        var delete = false;
                        while (negativeIndex < jv_array_length(jv_copy(negativeKeys)))
                        {
                            var deleteIndex = length + unchecked((int)
                                jv_number_get_value_and_consume(
                                    jv_array_get(jv_copy(negativeKeys), negativeIndex)));
                            if (index == deleteIndex)
                            {
                                delete = true;
                            }

                            if (index < deleteIndex)
                            {
                                break;
                            }

                            negativeIndex++;
                        }

                        while (nonnegativeIndex < jv_array_length(jv_copy(nonnegativeKeys)))
                        {
                            var deleteIndex = unchecked((int)
                                jv_number_get_value_and_consume(
                                    jv_array_get(jv_copy(nonnegativeKeys), nonnegativeIndex)));
                            if (index == deleteIndex)
                            {
                                delete = true;
                            }

                            if (index < deleteIndex)
                            {
                                break;
                            }

                            nonnegativeIndex++;
                        }

                        for (var sliceIndex = 0;
                             !delete && sliceIndex < jv_array_length(jv_copy(starts));
                             sliceIndex++)
                        {
                            if (unchecked((int)jv_number_get_value_and_consume(
                                    jv_array_get(jv_copy(starts), sliceIndex))) <= index &&
                                index < unchecked((int)jv_number_get_value_and_consume(
                                    jv_array_get(jv_copy(ends), sliceIndex))))
                            {
                                delete = true;
                            }
                        }

                        if (!delete)
                        {
                            result = jv_array_append(
                                result,
                                jv_array_get(jv_copy(target), index));
                        }
                    }
                }
            }
            finally
            {
                jv_free(negativeKeys);
                jv_free(nonnegativeKeys);
                jv_free(starts);
                jv_free(ends);
                jv_free(target);
                jv_free(keys);
            }

            return result;
        }

        if (target.Kind == jv_kind.JV_KIND_OBJECT)
        {
            var keyCount = jv_array_length(jv_copy(keys));
            for (var index = 0; index < keyCount; index++)
            {
                var key = jv_array_get(jv_copy(keys), index);
                if (key.Kind != jv_kind.JV_KIND_STRING)
                {
                    var kind = key.Kind;
                    jv_free(target);
                    target = jv_invalid_with_msg(jv_string(
                        "Cannot delete " + jv_kind_name(kind) + " field of object"));
                    jv_free(key);
                    break;
                }

                target = jv_object_delete(target, key);
            }

            jv_free(keys);
            return target;
        }

        var targetKind = target.Kind;
        jv_free(target);
        jv_free(keys);
        return jv_invalid_with_msg(jv_string(
            "Cannot delete fields from " + jv_kind_name(targetKind)));
    }

    private static double jv_number_get_value_and_consume(jv number)
    {
        var value = number.NumberValue;
        jv_free(number);
        return value;
    }

    // jq-1.8.2 src/jv_aux.c:parse_slice(), specialized only by its name to
    // make clear that jv_dels() owns the slice argument passed here.
    private static jv parse_slice(
        jv target,
        jv slice,
        out int start,
        out int end)
    {
        start = 0;
        end = 0;
        var startValue = jv_object_get(jv_copy(slice), jv_string("start"));
        var endValue = jv_object_get(slice, jv_string("end"));
        if (startValue.Kind == jv_kind.JV_KIND_NULL)
        {
            jv_free(startValue);
            startValue = jv_number(0);
        }

        int length;
        if (target.Kind == jv_kind.JV_KIND_ARRAY)
        {
            length = jv_array_length(target);
        }
        else if (target.Kind == jv_kind.JV_KIND_STRING)
        {
            length = jv_string_length_codepoints(target);
        }
        else
        {
            jv_free(target);
            jv_free(startValue);
            jv_free(endValue);
            return jv_invalid_with_msg(jv_string("Only arrays and strings can be sliced"));
        }

        if (endValue.Kind == jv_kind.JV_KIND_NULL)
        {
            jv_free(endValue);
            endValue = jv_number(length);
        }

        if (startValue.Kind != jv_kind.JV_KIND_NUMBER ||
            endValue.Kind != jv_kind.JV_KIND_NUMBER)
        {
            jv_free(startValue);
            jv_free(endValue);
            return jv_invalid_with_msg(
                jv_string("Array/string slice indices must be integers"));
        }

        var startNumber = startValue.NumberValue;
        var endNumber = endValue.NumberValue;
        jv_free(startValue);
        jv_free(endValue);

        if (double.IsNaN(startNumber))
        {
            startNumber = 0;
        }

        if (startNumber < 0)
        {
            startNumber += length;
        }

        if (startNumber < 0)
        {
            startNumber = 0;
        }

        if (startNumber > length)
        {
            startNumber = length;
        }

        start = startNumber > int.MaxValue ? int.MaxValue : unchecked((int)startNumber);

        if (double.IsNaN(endNumber))
        {
            endNumber = length;
        }

        if (endNumber < 0)
        {
            endNumber += length;
        }

        if (endNumber < 0)
        {
            endNumber = start;
        }

        end = endNumber > int.MaxValue ? int.MaxValue : unchecked((int)endNumber);
        if (end > length)
        {
            end = length;
        }

        if (end < length && end < endNumber)
        {
            end++;
        }

        if (end < start)
        {
            end = start;
        }

        return jv_true();
    }

    private sealed class DelpathsFrame(jv value, jv paths, int start)
    {
        internal jv Object { get; set; } = value;

        internal jv Paths { get; private set; } = paths;

        internal int Start { get; } = start;

        internal jv DeleteKeys { get; set; } = jv_array();

        internal int Index { get; set; }

        internal int PendingNextIndex { get; private set; }

        private jv pendingKey = jv_invalid();

        internal void SetPendingKey(jv key, int nextIndex)
        {
            pendingKey = key;
            PendingNextIndex = nextIndex;
        }

        internal jv TakePendingKey()
        {
            var key = pendingKey;
            pendingKey = jv_invalid();
            return key;
        }

        internal jv Complete()
        {
            var result = Object;
            Object = jv_invalid();
            jv_free(Paths);
            Paths = jv_invalid();
            if (result.IsValid)
            {
                var keys = DeleteKeys;
                DeleteKeys = jv_invalid();
                return jv_dels(result, keys);
            }

            jv_free(DeleteKeys);
            DeleteKeys = jv_invalid();
            return result;
        }

        internal void Release()
        {
            jv_free(Object);
            Object = jv_invalid();
            jv_free(Paths);
            Paths = jv_invalid();
            jv_free(DeleteKeys);
            DeleteKeys = jv_invalid();
            jv_free(pendingKey);
            pendingKey = jv_invalid();
        }
    }

    internal static jv jv_keys_unsorted(jv value)
    {
        if (value.Kind == jv_kind.JV_KIND_ARRAY)
        {
            return jv_keys(value);
        }

        if (value.Kind != jv_kind.JV_KIND_OBJECT)
        {
            throw new ArgumentException("jv_keys_unsorted requires an object or array.", nameof(value));
        }

        var result = jv_array_sized(jv_object_length(jv_copy(value)));
        for (var iterator = jv_object_iter(value);
             jv_object_iter_valid(value, iterator);
             iterator = jv_object_iter_next(value, iterator))
        {
            result = jv_array_append(result, jv_object_iter_key(value, iterator));
            jv_free(jv_object_iter_value(value, iterator));
        }

        jv_free(value);
        return result;
    }

    internal static jv jv_keys(jv value)
    {
        if (value.Kind == jv_kind.JV_KIND_OBJECT)
        {
            var keys = new List<jv>(jv_object_length(jv_copy(value)));
            for (var iterator = jv_object_iter(value);
                 jv_object_iter_valid(value, iterator);
                 iterator = jv_object_iter_next(value, iterator))
            {
                keys.Add(jv_object_iter_key(value, iterator));
                jv_free(jv_object_iter_value(value, iterator));
            }

            keys.Sort(static (left, right) => jvp_string_cmp(left, right));
            jv_free(value);
            return jv_array(keys);
        }

        if (value.Kind == jv_kind.JV_KIND_ARRAY)
        {
            var length = jv_array_length(value);
            return jv_array(
                Enumerable.Range(0, length).Select(static index => jv_number(index)));
        }

        throw new ArgumentException("jv_keys requires an object or array.", nameof(value));
    }

    // jq-1.8.2 src/jv_aux.c: sort_items()/jv_sort()/jv_group()/jv_unique().
    // The original index is the explicit tie-breaker, retaining jq's stable
    // ordering even when the platform sorting implementation changes.
    internal static jv jv_sort(jv objects, jv keys)
    {
        if (!TrySortItems(objects, keys, out var entries, out var error))
        {
            return error;
        }

        var result = jv_array_sized(entries.Length);
        foreach (var entry in entries)
        {
            jv_free(entry.Key);
            result = jv_array_append(result, entry.Value);
        }

        return result;
    }

    internal static jv jv_group(jv objects, jv keys)
    {
        if (!TrySortItems(objects, keys, out var entries, out var error))
        {
            return error;
        }

        var result = jv_array();
        if (entries.Length == 0)
        {
            return result;
        }

        // jq-1.8.2 src/jv_aux.c:jv_group(): the first entry's owners move
        // into curr_key/group; each later key is either freed or becomes the
        // next curr_key, and every object moves into exactly one group.
        var currentKey = entries[0].Key;
        var group = jv_array_append(jv_array(), entries[0].Value);
        for (var index = 1; index < entries.Length; index++)
        {
            bool equal;
            try
            {
                equal = jv_equal(jv_copy(currentKey), jv_copy(entries[index].Key));
            }
            catch (JqRuntimeException exception) when (exception.Message == "Equality check too deep")
            {
                jv_free(currentKey);
                jv_free(group);
                FreeSortEntries(entries, index);
                jv_free(result);
                return jv_invalid_with_msg(jv_string("Equality check too deep"));
            }

            if (equal)
            {
                jv_free(entries[index].Key);
            }
            else
            {
                jv_free(currentKey);
                currentKey = entries[index].Key;
                result = jv_array_append(result, group);
                group = jv_array();
            }

            group = jv_array_append(group, entries[index].Value);
        }

        jv_free(currentKey);
        return jv_array_append(result, group);
    }

    internal static jv jv_unique(jv objects, jv keys)
    {
        if (!TrySortItems(objects, keys, out var entries, out var error))
        {
            return error;
        }

        var result = jv_array();
        var currentKey = jv_invalid();
        for (var index = 0; index < entries.Length; index++)
        {
            bool equal;
            try
            {
                equal = jv_equal(jv_copy(currentKey), jv_copy(entries[index].Key));
            }
            catch (JqRuntimeException exception) when (exception.Message == "Equality check too deep")
            {
                jv_free(currentKey);
                FreeSortEntries(entries, index);
                jv_free(result);
                return jv_invalid_with_msg(jv_string("Equality check too deep"));
            }

            if (equal)
            {
                jv_free(entries[index].Key);
                jv_free(entries[index].Value);
            }
            else
            {
                jv_free(currentKey);
                currentKey = entries[index].Key;
                result = jv_array_append(result, entries[index].Value);
            }
        }

        jv_free(currentKey);
        return result;
    }

    private static bool TrySortItems(
        jv objects,
        jv keys,
        out JvSortEntry[] entries,
        out jv error)
    {
        if (objects.Kind != jv_kind.JV_KIND_ARRAY ||
            keys.Kind != jv_kind.JV_KIND_ARRAY ||
            objects.ArrayValue.Count != keys.ArrayValue.Count)
        {
            throw new ArgumentException("jv keyed operations require equal-length arrays.");
        }

        entries = objects.ArrayValue
            .Select((value, index) => new JvSortEntry(
                jv_copy(value),
                jv_copy(keys.ArrayValue[index]),
                index))
            .ToArray();
        jv_free(objects);
        jv_free(keys);
        try
        {
            Array.Sort(entries, CompareSortEntries);
            error = jv_invalid();
            return true;
        }
        catch (JqRuntimeException exception) when (exception.Message == "Comparison too deep")
        {
            FreeSortEntries(entries, 0);
            entries = [];
            error = jv_invalid_with_msg(jv_string("Comparison too deep"));
            return false;
        }
        catch (InvalidOperationException exception)
            when (exception.InnerException is JqRuntimeException { Message: "Comparison too deep" })
        {
            FreeSortEntries(entries, 0);
            entries = [];
            error = jv_invalid_with_msg(jv_string("Comparison too deep"));
            return false;
        }
    }

    private static int CompareSortEntries(JvSortEntry left, JvSortEntry right)
    {
        var comparison = jv_cmp(jv_copy(left.Key), jv_copy(right.Key));
        return comparison != 0 ? comparison : left.Index.CompareTo(right.Index);
    }

    private static void FreeSortEntries(JvSortEntry[] entries, int start)
    {
        for (var index = start; index < entries.Length; index++)
        {
            jv_free(entries[index].Key);
            jv_free(entries[index].Value);
        }
    }

    private readonly record struct JvSortEntry(jv Value, jv Key, int Index);

    internal static bool IsInteger(double value) =>
        double.IsFinite(value) && value == Math.Truncate(value);

    private static int CompareNumbers(jv left, jv right)
    {
        // jq-1.8.2 src/jv_aux.c:jvp_cmp() orders NaN as null and delegates
        // every other numeric comparison to jvp_number_cmp(). Keep this
        // canonical route so literal/literal comparison never projects to
        // binary64 and mixed comparisons use the source-shaped lazy bridge.
        if (jvp_number_is_nan(left))
        {
            // Upstream recurses as jvp_cmp(null, copy(right)).  Because the
            // kinds then differ, this is negative even when right is another
            // NaN number; jq consequently reports `nan < nan` as true.
            return -1;
        }

        if (jvp_number_is_nan(right))
        {
            return 1;
        }

        return jvp_number_cmp(left, right);
    }

}
// Managed helper boundary for src/jv_aux.c:jv_setpath/jv_delpaths and the
// consuming jv_get/jv_set index/slice operations they share.
internal static class PathUpdates
{
    private const int MaximumPathDepth = 10_000;

    internal static jv NormalizeIndexComponent(jv component)
    {
        if (component.Kind != jv_kind.JV_KIND_NUMBER ||
            !double.IsFinite(component.NumberValue))
        {
            return component;
        }

        return libjq.jv_number(Math.Truncate(component.NumberValue));
    }

    internal static jv SliceComponent(double? start, double? end)
    {
        var result = libjq.jv_object();
        result = libjq.jv_object_set(
            result,
            "start",
            start is { } startValue ? libjq.jv_number(startValue) : libjq.jv_null());
        return libjq.jv_object_set(
            result,
            "end",
            end is { } endValue ? libjq.jv_number(endValue) : libjq.jv_null());
    }

    internal static jv GetAtPath(jv root, IReadOnlyList<jv> path)
        => GetAtPathOwned(libjq.jv_copy(root), path);

    internal static jv GetAtPathOwned(jv root, IReadOnlyList<jv> path)
    {
        if (path.Count > MaximumPathDepth)
        {
            libjq.jv_free(root);
            throw new JqRuntimeException("Path too deep");
        }

        // jq-1.8.2 src/execute.c INDEX/INDEX_OPT pops (owns) `t`, then calls
        // jv_get(t, jv_copy(k)). This helper's root/path inputs are borrowed,
        // so acquire the same owned `t` at this boundary.
        var current = root;
        foreach (var component in path)
        {
            if (TryGetSlice(component, out var start, out var end))
            {
                if (current.Kind == jv_kind.JV_KIND_ARRAY)
                {
                    var (from, to) = NormalizeSlice(start, end, current.ArrayValue.Count);
                    current = libjq.jv_array_slice(current, from, to);
                }
                else
                {
                    var consumed = current;
                    current = SliceValue(consumed, start, end);
                    libjq.jv_free(consumed);
                }

                continue;
            }

            // jq-1.8.2 src/execute.c:695: jv_get(t, jv_copy(k)).
            current = GetOwnedComponent(current, libjq.jv_copy(component));
        }

        return current;
    }

    internal static jv SetAtPath(jv root, IReadOnlyList<jv> path, jv value)
    {
        // The caller retains root and value. Acquire both owners before
        // entering the source-shaped operation, which consumes both arguments.
        return SetAtPathOwned(
            libjq.jv_copy(root),
            path,
            libjq.jv_copy(value));
    }

    // jq-1.8.2 src/jv_aux.c:jv_setpath() consumes root and value and borrows
    // no path components: native pathcurr owns copies extracted from path.
    // Our IReadOnlyList is a borrowed view, so each consuming key operation
    // receives jv_copy(component) at the same source boundary.
    internal static jv SetAtPathOwned(jv root, IReadOnlyList<jv> path, jv value)
    {
        if (path.Count > MaximumPathDepth)
        {
            libjq.jv_free(root);
            libjq.jv_free(value);
            throw new JqRuntimeException("Path too deep");
        }

        if (!root.IsValid)
        {
            libjq.jv_free(value);
            return root;
        }

        if (path.Count == 0)
        {
            libjq.jv_free(root);
            return value;
        }

        // jq-1.8.2 src/jv_aux.c:jv_setpath() recursively descends, clears
        // each selected child to null, then rebuilds on return.  Ten thousand
        // small C frames fit jq's supported MAX_PATH_DEPTH; equivalent CLR
        // frames do not.  This explicit stack preserves that exact order and
        // the source's copy/get/set ownership boundaries without CLR recursion.
        var frames = new SetPathFrame[path.Count - 1];
        var frameCount = 0;
        var current = root;
        var currentOwned = true;
        var valueOwned = true;
        try
        {
            for (var offset = 0; offset < path.Count - 1; offset++)
            {
                var component = path[offset];
                if (TryGetSlice(component, out var start, out var end))
                {
                    // Native slice paths deliberately keep the outer root while
                    // jv_get(jv_copy(root), jv_copy(pathcurr)) obtains the child.
                    var child = SliceForSetPath(current, start, end);
                    frames[offset] = SetPathFrame.ForSlice(current, start, end);
                    frameCount++;
                    current = child;
                    continue;
                }

                if (component.Kind == jv_kind.JV_KIND_STRING)
                {
                    if (current.Kind == jv_kind.JV_KIND_NULL)
                    {
                        current = libjq.jv_object();
                    }
                    else if (current.Kind != jv_kind.JV_KIND_OBJECT)
                    {
                        throw IndexError(current, component);
                    }

                    // jv_aux.c:427-438: copy/get the subroot, then replace the
                    // retained root edge with null before descending.
                    // Keep the raw UTF-8 jv key: native pathcurr is copied out of
                    // the path array, and both object operations consume their key.
                    var child = libjq.jv_get(
                        libjq.jv_copy(current),
                        libjq.jv_copy(component),
                        optional: false);
                    current = libjq.jv_object_set(
                        current,
                        libjq.jv_copy(component),
                        libjq.jv_null());
                    if (!current.IsValid)
                    {
                        libjq.jv_free(child);
                        currentOwned = false;
                        throw UpdateError(current);
                    }

                    frames[offset] = SetPathFrame.ForComponent(current, component);
                    frameCount++;
                    current = child;
                    continue;
                }

                if (component.Kind == jv_kind.JV_KIND_NUMBER)
                {
                    if (current.Kind == jv_kind.JV_KIND_NULL)
                    {
                        current = libjq.jv_array();
                    }
                    else if (current.Kind != jv_kind.JV_KIND_ARRAY)
                    {
                        throw IndexError(current, component);
                    }

                    component = ValidateAndNormalizeArraySetComponent(
                        component,
                        libjq.jvp_array_offset(current));

                    // jq-1.8.2 src/jv_aux.c:427,438:
                    //   subroot = jv_get(jv_copy(root), jv_copy(pathcurr));
                    //   root = jv_set(root, jv_copy(pathcurr), jv_null());
                    var child = libjq.jv_get(
                        libjq.jv_copy(current),
                        libjq.jv_copy(component));
                    current = libjq.jv_array_set(
                        current,
                        checked((int)component.NumberValue),
                        libjq.jv_null());
                    if (!current.IsValid)
                    {
                        libjq.jv_free(child);
                        currentOwned = false;
                        throw UpdateError(current);
                    }

                    frames[offset] = SetPathFrame.ForComponent(current, component);
                    frameCount++;
                    current = child;
                    continue;
                }

                throw InvalidUpdateComponent(current, component);
            }

            // SetPathLeaf consumes both arguments on success and failure.
            currentOwned = false;
            valueOwned = false;
            var replacement = SetPathLeaf(current, path[^1], value);
            if (!replacement.IsValid)
            {
                throw UpdateError(replacement);
            }

            for (var offset = frames.Length - 1; offset >= 0; offset--)
            {
                var frame = frames[offset];
                frames[offset] = default;
                frameCount--;
                replacement = frame.IsSlice
                    ? ReplaceSlice(frame.Container, frame.Start, frame.End, replacement)
                    : frame.Component.Kind == jv_kind.JV_KIND_STRING
                        ? libjq.jv_object_set(
                            frame.Container,
                            libjq.jv_copy(frame.Component),
                            replacement)
                        : libjq.jv_array_set(
                            frame.Container,
                            checked((int)frame.Component.NumberValue),
                            replacement);
                if (!replacement.IsValid)
                {
                    throw UpdateError(replacement);
                }
            }

            return replacement;
        }
        catch
        {
            if (currentOwned)
            {
                libjq.jv_free(current);
            }

            if (valueOwned)
            {
                libjq.jv_free(value);
            }

            for (var offset = 0; offset < frameCount; offset++)
            {
                libjq.jv_free(frames[offset].Container);
            }

            throw;
        }
    }

    internal static jv DeletePaths(
        jv root,
        IReadOnlyList<IReadOnlyList<jv>> paths)
    {
        // Managed path resolvers expose borrowed component lists. Materialize
        // only the owned jv array expected by jq-1.8.2 jv_delpaths(), then let
        // that source-shaped iterative port perform sorting, grouping, slice
        // deletion, and deep traversal. This removes the former recursive
        // ExpandDeletionPath/DeleteAtPath CLR-stack dependency.
        var ownedPaths = libjq.jv_array_sized(paths.Count);
        var rootOwned = true;
        try
        {
            foreach (var path in paths)
            {
                var ownedPath = libjq.jv_array_sized(path.Count);
                try
                {
                    foreach (var component in path)
                    {
                        ownedPath = libjq.jv_array_append(
                            ownedPath,
                            libjq.jv_copy(component));
                    }

                    ownedPaths = libjq.jv_array_append(ownedPaths, ownedPath);
                    ownedPath = libjq.jv_invalid();
                }
                finally
                {
                    libjq.jv_free(ownedPath);
                }
            }

            var consumedPaths = ownedPaths;
            rootOwned = false;
            ownedPaths = libjq.jv_invalid();
            var result = libjq.jv_delpaths(root, consumedPaths);
            if (result.IsValid)
            {
                return result;
            }

            if (libjq.jv_invalid_has_msg(libjq.jv_copy(result)))
            {
                throw new JqRuntimeException(libjq.jv_invalid_get_msg(result));
            }

            libjq.jv_free(result);
            throw new JqRuntimeException("jq path deletion returned an invalid value");
        }
        finally
        {
            if (rootOwned)
            {
                libjq.jv_free(root);
            }

            libjq.jv_free(ownedPaths);
        }
    }

    private static jv GetOwnedComponent(jv target, jv key)
    {
        // jq-1.8.2 src/execute.c:695 passes owned `t` and `jv_copy(k)` to
        // jv_get(), which consumes both on every success/error branch.  Do not
        // decode string keys and enter the borrowing System.String adapter:
        // that loses the consuming boundary (leaking both owners) and breaks
        // raw UTF-8 key allocation identity used by jq's refcount/COW model.
        return libjq.jv_get(target, key, optional: false);
    }

    private static jv SliceForSetPath(jv current, double? start, double? end)
    {
        if (current.Kind == jv_kind.JV_KIND_ARRAY)
        {
            var (from, to) = NormalizeSlice(start, end, current.ArrayValue.Count);
            return libjq.jv_array_slice(libjq.jv_copy(current), from, to);
        }

        return SliceValue(current, start, end);
    }

    private static jv SetPathLeaf(jv current, jv component, jv value)
    {
        if (TryGetSlice(component, out var start, out var end))
        {
            return ReplaceSlice(current, start, end, value);
        }

        if (component.Kind == jv_kind.JV_KIND_STRING)
        {
            if (current.Kind == jv_kind.JV_KIND_NULL)
            {
                current = libjq.jv_object();
            }
            else if (current.Kind != jv_kind.JV_KIND_OBJECT)
            {
                var exception = IndexError(current, component);
                libjq.jv_free(current);
                libjq.jv_free(value);
                throw exception;
            }

            // jv_setpath() owns pathcurr separately from the path array and
            // moves that raw jv string into a newly-created object slot.
            return libjq.jv_object_set(current, libjq.jv_copy(component), value);
        }

        if (component.Kind == jv_kind.JV_KIND_NUMBER)
        {
            if (current.Kind == jv_kind.JV_KIND_NULL)
            {
                current = libjq.jv_array();
            }
            else if (current.Kind != jv_kind.JV_KIND_ARRAY)
            {
                var exception = IndexError(current, component);
                libjq.jv_free(current);
                libjq.jv_free(value);
                throw exception;
            }

            try
            {
                component = ValidateAndNormalizeArraySetComponent(
                    component,
                    libjq.jvp_array_offset(current));
            }
            catch
            {
                libjq.jv_free(current);
                libjq.jv_free(value);
                throw;
            }

            return libjq.jv_array_set(
                current,
                checked((int)component.NumberValue),
                value);
        }

        var invalidComponent = InvalidUpdateComponent(current, component);
        libjq.jv_free(current);
        libjq.jv_free(value);
        throw invalidComponent;
    }

    private static jv ValidateAndNormalizeArraySetComponent(jv component, int arrayOffset)
    {
        if (double.IsNaN(component.NumberValue))
        {
            throw new JqRuntimeException("Cannot set array element at NaN index");
        }

        var truncatedIndex = Math.Truncate(component.NumberValue);
        if (double.IsNegativeInfinity(truncatedIndex) || truncatedIndex < int.MinValue)
        {
            throw new JqRuntimeException("Out of bounds negative array index");
        }

        // jq-1.8.2 src/jv.c:jv_array_set() checks the logical index against
        // `(INT_MAX >> 2) - jvp_array_offset(j)`. The former int.MaxValue / 16
        // pre-cap rejected valid source indices four times too early.
        if (libjq.jvp_array_set_index_too_large(truncatedIndex, arrayOffset))
        {
            throw new JqRuntimeException("Array index too large");
        }

        return NormalizeIndexComponent(component);
    }

    private static JqRuntimeException InvalidUpdateComponent(jv current, jv component) =>
        new(
            "Cannot update field at " + libjq.jv_kind_name(component.Kind) +
            " index of " + libjq.jv_kind_name(current.Kind));

    private static JqRuntimeException UpdateError(jv invalid) =>
        new(libjq.jv_invalid_get_msg(invalid));

    private readonly record struct SetPathFrame(
        jv Container,
        jv Component,
        bool IsSlice,
        double? Start,
        double? End)
    {
        internal static SetPathFrame ForComponent(jv container, jv component) =>
            new(container, component, IsSlice: false, Start: null, End: null);

        internal static SetPathFrame ForSlice(jv container, double? start, double? end) =>
            new(container, default, IsSlice: true, start, end);
    }

    private static jv ReplaceSlice(jv current, double? start, double? end, jv replacement)
    {
        // jq's setpath treats null as the empty aggregate selected by the path
        // component.  A slice replacement therefore materializes an empty array,
        // just as a string key materializes an object and an integer key an array.
        if (current.Kind == jv_kind.JV_KIND_NULL)
        {
            current = libjq.jv_array();
        }

        if (current.Kind == jv_kind.JV_KIND_STRING)
        {
            libjq.jv_free(current);
            libjq.jv_free(replacement);
            throw new JqRuntimeException("Cannot update string slices");
        }

        if (current.Kind != jv_kind.JV_KIND_ARRAY)
        {
            var kind = libjq.jv_kind_name(current.Kind);
            libjq.jv_free(current);
            libjq.jv_free(replacement);
            throw new JqRuntimeException("Cannot update " + kind + " slices");
        }

        if (replacement.Kind != jv_kind.JV_KIND_ARRAY)
        {
            libjq.jv_free(current);
            libjq.jv_free(replacement);
            throw new JqRuntimeException("A slice of an array can only be assigned another array");
        }

        // jq-1.8.2 src/jv_aux.c:jv_set(), array-slice branch.  Preserve its
        // element move/copy order so nested refcounts and COW decisions match.
        var (from, to) = NormalizeSlice(start, end, current.ArrayValue.Count);
        var arrayLength = libjq.jv_array_length(libjq.jv_copy(current));
        var sliceLength = to - from;
        var insertLength = libjq.jv_array_length(libjq.jv_copy(replacement));
        if (sliceLength < insertLength)
        {
            var shift = insertLength - sliceLength;
            for (var index = arrayLength - 1; index >= to && current.IsValid; index--)
            {
                current = libjq.jv_array_set(
                    current,
                    index + shift,
                    libjq.jv_array_get(libjq.jv_copy(current), index));
            }
        }
        else if (sliceLength > insertLength)
        {
            var shift = sliceLength - insertLength;
            for (var index = to; index < arrayLength && current.IsValid; index++)
            {
                current = libjq.jv_array_set(
                    current,
                    index - shift,
                    libjq.jv_array_get(libjq.jv_copy(current), index));
            }

            if (current.IsValid)
            {
                current = libjq.jv_array_slice(current, 0, arrayLength - shift);
            }
        }

        for (var index = 0; index < insertLength && current.IsValid; index++)
        {
            current = libjq.jv_array_set(
                current,
                from + index,
                libjq.jv_array_get(libjq.jv_copy(replacement), index));
        }

        libjq.jv_free(replacement);
        return current;
    }

    private static bool TryGetSlice(jv component, out double? start, out double? end)
    {
        start = null;
        end = null;
        if (component.Kind != jv_kind.JV_KIND_OBJECT ||
            !libjq.jv_object_has(component, "start") ||
            !libjq.jv_object_has(component, "end"))
        {
            return false;
        }

        start = ReadSliceBound(libjq.jv_object_get(component, "start"));
        end = ReadSliceBound(libjq.jv_object_get(component, "end"));
        return true;
    }

    private static double? ReadSliceBound(jv value)
    {
        if (value.Kind == jv_kind.JV_KIND_NULL)
        {
            return null;
        }

        if (value.Kind == jv_kind.JV_KIND_NUMBER)
        {
            return value.NumberValue;
        }

        throw new JqRuntimeException("Array slice indices must be integers");
    }

    private static JqRuntimeException IndexError(jv target, jv key) =>
        new(
            "Cannot index " + libjq.jv_kind_name(target.Kind) + " with " +
            libjq.jv_kind_name(key.Kind) + " (" +
            libjq.jv_dump_string_borrowed(key) + ")");


    private static jv SliceValue(jv value, double? start, double? end) =>
        SliceValueOwned(libjq.jv_copy(value), start, end);

    private static jv SliceValueOwned(jv value, double? start, double? end)
    {
        if (value.Kind == jv_kind.JV_KIND_ARRAY)
        {
            var (from, to) = NormalizeSlice(start, end, value.ArrayValue.Count);
            return libjq.jv_array_slice(value, from, to);
        }

        if (value.Kind == jv_kind.JV_KIND_STRING)
        {
            var length = libjq.jv_string_length_codepoints(libjq.jv_copy(value));
            var (from, to) = NormalizeSlice(start, end, length);
            return libjq.jv_string_slice(value, from, to);
        }

        if (value.Kind == jv_kind.JV_KIND_NULL)
        {
            libjq.jv_free(value);
            return libjq.jv_null();
        }

        var kind = libjq.jv_kind_name(value.Kind);
        libjq.jv_free(value);
        throw new JqRuntimeException("Cannot slice " + kind);
    }

    private static (int Start, int End) NormalizeSlice(
        double? start,
        double? end,
        int length)
    {
        var normalizedStart = NormalizeSliceBound(start, length, isEnd: false);
        var normalizedEnd = NormalizeSliceBound(end, length, isEnd: true);
        return normalizedEnd < normalizedStart
            ? (normalizedStart, normalizedStart)
            : (normalizedStart, normalizedEnd);
    }

    private static int NormalizeSliceBound(double? value, int length, bool isEnd)
    {
        if (value is null || double.IsNaN(value.Value))
        {
            return isEnd ? length : 0;
        }

        var rounded = isEnd ? Math.Ceiling(value.Value) : Math.Floor(value.Value);
        if (double.IsNegativeInfinity(rounded))
        {
            return 0;
        }

        if (double.IsPositiveInfinity(rounded))
        {
            return length;
        }

        if (rounded < 0)
        {
            rounded += length;
        }

        return checked((int)Math.Clamp(rounded, 0, length));
    }

}
