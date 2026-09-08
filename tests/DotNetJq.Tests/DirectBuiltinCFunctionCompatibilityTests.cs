using DotNetJq.Port;

namespace DotNetJq.Tests;

public sealed class DirectBuiltinCFunctionCompatibilityTests
{
    private const string ExpectedFunctionList =
        "acos/1,acosh/1,asin/1,asinh/1,atan/1,atan2/3,atanh/1,cbrt/1,cos/1,cosh/1," +
        "exp/1,exp2/1,floor/1,hypot/3,j0/1,j1/1,log/1,log10/1,log2/1,pow/3," +
        "remainder/3,sin/1,sinh/1,sqrt/1,tan/1,tanh/1,tgamma/1,y0/1,y1/1,jn/3,yn/3," +
        "ceil/1,copysign/3,drem/3,erf/1,erfc/1,exp10/1,expm1/1,fabs/1,fdim/3,fma/4," +
        "fmax/3,fmin/3,fmod/3,gamma/1,lgamma/1,log1p/1,logb/1,nearbyint/1," +
        "nextafter/3,nexttoward/3,rint/1,round/1,scalb/3,scalbln/3,significand/1," +
        "trunc/1,ldexp/3,modf/1,frexp/1,lgamma_r/1,_negate/1,_plus/3,_minus/3," +
        "_multiply/3,_divide/3,_mod/3,_equal/3,_notequal/3,_less/3,_lesseq/3," +
        "_greater/3,_greatereq/3,tojson/1,fromjson/1,tonumber/1,toboolean/1,tostring/1," +
        "keys/1,keys_unsorted/1,startswith/2,endswith/2,split/2,explode/1,implode/1," +
        "_strindices/2,trim/1,ltrim/1,rtrim/1,setpath/3,getpath/2,delpaths/2,has/2," +
        "contains/2,length/1,utf8bytelength/1,type/1,isinfinite/1,isnan/1,isnormal/1," +
        "infinite/1,nan/1,sort/1,_sort_by_impl/2,_group_by_impl/2,unique/1," +
        "_unique_by_impl/2,bsearch/2,min/1,max/1,_min_by_impl/2,_max_by_impl/2,error/1," +
        "format/2,env/1,halt/1,halt_error/2,get_search_list/1,get_prog_origin/1," +
        "get_jq_origin/1,_match_impl/4,modulemeta/1,input/1,debug/1,stderr/1,strptime/2," +
        "strftime/2,strflocaltime/2,mktime/1,gmtime/1,localtime/1,now/1,input_filename/1," +
        "input_line_number/1,have_decnum/1,have_literal_numbers/1";

    [Fact]
    public void FunctionListMatchesJq182ExpandedSourceOrderAndArities()
    {
        var expected = ExpectedFunctionList.Split(',');

        Assert.Equal(136, expected.Length);
        Assert.Equal(
            expected,
            libjq.function_list.Select(static function => $"{function.name}/{function.nargs}"));
    }

    [Fact]
    public void FunctionListPopulatesOnlyTheDelegateSlotMatchingNargs()
    {
        foreach (var function in libjq.function_list)
        {
            Assert.NotNull(function.fptr.for_arity(function.nargs));
            Assert.Equal(function.nargs == 1, function.fptr.a1 is not null);
            Assert.Equal(function.nargs == 2, function.fptr.a2 is not null);
            Assert.Equal(function.nargs == 3, function.fptr.a3 is not null);
            Assert.Equal(function.nargs == 4, function.fptr.a4 is not null);
        }
    }

    [Fact]
    public void IdentityReturningFunctionsMoveTheInputOwnerWithoutCopying()
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_string("unchanged");
        var alias = libjq.jv_copy(input);
        var tostring = Find("tostring").fptr.a1!;

        var result = tostring(jq, input);

        Assert.Same(alias.Value, result.Value);
        Assert.Equal(2, libjq.jv_get_refcnt(result));
        libjq.jv_free(alias);
        libjq.jv_free(result);
    }

    [Fact]
    public void UnchangedTrimMovesTheOriginalStringAllocation()
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_string("already-trimmed");
        var alias = libjq.jv_copy(input);

        var result = Find("trim").fptr.a1!(jq, input);

        Assert.Same(alias.Value, result.Value);
        Assert.Equal(2, libjq.jv_get_refcnt(result));
        libjq.jv_free(alias);
        libjq.jv_free(result);
    }

    [Fact]
    public void TypeErrorConsumesArgumentsButLeavesIndependentCopiesAlive()
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var prefix = libjq.jv_string("x");
        var inputAlias = libjq.jv_copy(input);
        var prefixAlias = libjq.jv_copy(prefix);

        var error = Find("startswith").fptr.a2!(jq, input, prefix);

        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(error)));
        Assert.Equal(1, libjq.jv_get_refcnt(inputAlias));
        Assert.Equal(1, libjq.jv_get_refcnt(prefixAlias));
        Assert.Equal("[1]", libjq.jv_dump_string_borrowed(inputAlias));
        Assert.Equal("x", prefixAlias.StringValue);
        libjq.jv_free(error);
        libjq.jv_free(inputAlias);
        libjq.jv_free(prefixAlias);
    }

    [Fact]
    public void SortConsumesItsOwnerAndDetachesFromAnAliasedInput()
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(3), libjq.jv_number(1), libjq.jv_number(2)]);
        var alias = libjq.jv_copy(input);

        var result = Find("sort").fptr.a1!(jq, input);

        Assert.Equal("[1,2,3]", libjq.jv_dump_string_borrowed(result));
        Assert.Equal("[3,1,2]", libjq.jv_dump_string_borrowed(alias));
        Assert.Equal(1, libjq.jv_get_refcnt(alias));
        libjq.jv_free(result);
        libjq.jv_free(alias);
    }

    [Fact]
    public void GetpathMovesBothArgumentsThroughTheVmPathHook()
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_object(
            [KeyValuePair.Create("a", libjq.jv_number(7))]);
        var path = libjq.jv_array([libjq.jv_string("a")]);
        var inputAlias = libjq.jv_copy(input);
        var pathAlias = libjq.jv_copy(path);

        var result = Find("getpath").fptr.a2!(jq, input, path);

        Assert.Equal("7", libjq.jv_dump_string_borrowed(result));
        Assert.Equal(1, libjq.jv_get_refcnt(inputAlias));
        Assert.Equal(1, libjq.jv_get_refcnt(pathAlias));
        libjq.jv_free(result);
        libjq.jv_free(inputAlias);
        libjq.jv_free(pathAlias);
    }

    [Fact]
    public void DelpathsSuccessConsumesBothArgumentsOnce()
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_object(
            [KeyValuePair.Create("a", libjq.jv_number(7))]);
        var paths = libjq.jv_array(
            [libjq.jv_array([libjq.jv_string("a")])]);
        var inputAlias = libjq.jv_copy(input);
        var pathsAlias = libjq.jv_copy(paths);

        var result = Find("delpaths").fptr.a2!(jq, input, paths);

        Assert.Equal("{}", libjq.jv_dump_string_borrowed(result));
        Assert.Equal("{\"a\":7}", libjq.jv_dump_string_borrowed(inputAlias));
        Assert.Equal(1, libjq.jv_get_refcnt(inputAlias));
        Assert.Equal(1, libjq.jv_get_refcnt(pathsAlias));
        libjq.jv_free(result);
        libjq.jv_free(inputAlias);
        libjq.jv_free(pathsAlias);
    }

    [Fact]
    public void DelpathsEmptyListMovesInputAndConsumesPaths()
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_array([libjq.jv_number(1)]);
        var paths = libjq.jv_array();
        var inputAlias = libjq.jv_copy(input);
        var pathsAlias = libjq.jv_copy(paths);

        var result = Find("delpaths").fptr.a2!(jq, input, paths);

        Assert.Same(inputAlias.Value, result.Value);
        Assert.Equal(2, libjq.jv_get_refcnt(result));
        Assert.Equal(1, libjq.jv_get_refcnt(pathsAlias));
        libjq.jv_free(result);
        libjq.jv_free(inputAlias);
        libjq.jv_free(pathsAlias);
    }

    [Fact]
    public void DelpathsValidationErrorsConsumeBothArguments()
    {
        using var jq = libjq.jq_init();
        var input = libjq.jv_object();
        var paths = libjq.jv_array([libjq.jv_number(0)]);
        var inputAlias = libjq.jv_copy(input);
        var pathsAlias = libjq.jv_copy(paths);

        var result = Find("delpaths").fptr.a2!(jq, input, paths);

        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(result)));
        Assert.Equal(1, libjq.jv_get_refcnt(inputAlias));
        Assert.Equal(1, libjq.jv_get_refcnt(pathsAlias));
        libjq.jv_free(result);
        libjq.jv_free(inputAlias);
        libjq.jv_free(pathsAlias);
    }

    [Fact]
    public void DelpathsDeepPathAndInvalidInputConsumeBothArguments()
    {
        using var jq = libjq.jq_init();
        var deepPath = libjq.jv_array();
        for (var index = 0; index < 10_001; index++)
        {
            deepPath = libjq.jv_array_append(deepPath, libjq.jv_number(0));
        }

        var deepPaths = libjq.jv_array([deepPath]);
        var deepInput = libjq.jv_null();
        var deepPathsAlias = libjq.jv_copy(deepPaths);
        var deepResult = Find("delpaths").fptr.a2!(jq, deepInput, deepPaths);
        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(deepResult)));
        Assert.Equal(1, libjq.jv_get_refcnt(deepPathsAlias));
        libjq.jv_free(deepResult);
        libjq.jv_free(deepPathsAlias);

        var invalidInput = libjq.jv_number(1);
        var invalidPaths = libjq.jv_array(
            [libjq.jv_array([libjq.jv_number(0)])]);
        var invalidPathsAlias = libjq.jv_copy(invalidPaths);
        var invalidResult = Find("delpaths").fptr.a2!(jq, invalidInput, invalidPaths);
        Assert.True(libjq.jv_invalid_has_msg(libjq.jv_copy(invalidResult)));
        Assert.Equal(1, libjq.jv_get_refcnt(invalidPathsAlias));
        libjq.jv_free(invalidResult);
        libjq.jv_free(invalidPathsAlias);
    }

    [Fact]
    public void DirectBuiltinsBindParsesEmbeddedLibraryAndResolvesCFunction()
    {
        using var jq = libjq.jq_init();
        var program = libjq.gen_call("length", libjq.gen_noop());

        Assert.Equal(0, libjq.builtins_bind(jq, ref program));
        Assert.Contains(
            Enumerate(program),
            static instruction =>
                instruction.op == opcode.CLOSURE_CREATE_C &&
                instruction.symbol == "length");

        libjq.block_free(program);
    }

    [Fact]
    public void DirectTimeBuiltinsConsumeTheirJvArgumentsOnce()
    {
        using var jq = libjq.jq_init();

        var dateText = libjq.jv_string("1970-01-01 00:00:00");
        var parseFormat = libjq.jv_string("%Y-%m-%d %H:%M:%S");
        var dateTextAlias = libjq.jv_copy(dateText);
        var parseFormatAlias = libjq.jv_copy(parseFormat);
        var parsed = Find("strptime").fptr.a2!(jq, dateText, parseFormat);
        Assert.Equal(1, libjq.jv_get_refcnt(dateTextAlias));
        Assert.Equal(1, libjq.jv_get_refcnt(parseFormatAlias));

        var parsedAlias = libjq.jv_copy(parsed);
        var epoch = Find("mktime").fptr.a1!(jq, parsed);
        Assert.Equal(0, epoch.NumberValue);
        Assert.Equal(1, libjq.jv_get_refcnt(parsedAlias));

        var epochAlias = libjq.jv_copy(epoch);
        var brokenDown = Find("gmtime").fptr.a1!(jq, epoch);
        Assert.Equal(1, libjq.jv_get_refcnt(epochAlias));

        var format = libjq.jv_string("%Y-%m-%d");
        var brokenDownAlias = libjq.jv_copy(brokenDown);
        var formatAlias = libjq.jv_copy(format);
        var formatted = Find("strftime").fptr.a2!(jq, brokenDown, format);
        Assert.Equal("1970-01-01", formatted.StringValue);
        Assert.Equal(1, libjq.jv_get_refcnt(brokenDownAlias));
        Assert.Equal(1, libjq.jv_get_refcnt(formatAlias));

        libjq.jv_free(formatted);
        libjq.jv_free(formatAlias);
        libjq.jv_free(brokenDownAlias);
        libjq.jv_free(epochAlias);
        libjq.jv_free(parsedAlias);
        libjq.jv_free(parseFormatAlias);
        libjq.jv_free(dateTextAlias);
    }

    [Fact]
    public void DirectLibmDelegatesRouteThroughThePinnedCompatibilityKernels()
    {
        using var jq = libjq.jq_init();
        (string Name, double Input, double Expected)[] cases =
        [
            ("acosh", 1.5, libjq.jq_acosh(1.5)),
            ("asinh", -0.75, libjq.jq_asinh(-0.75)),
            ("atanh", 0.25, libjq.jq_atanh(0.25)),
            ("expm1", -0.125, libjq.jq_expm1(-0.125)),
            ("log1p", 0.125, libjq.jq_log1p(0.125)),
            ("j0", 11.75, libjq.jq_j0(11.75)),
            ("y1", 11.75, libjq.jq_y1(11.75)),
            ("lgamma", -2.5, libjq.jq_lgamma(-2.5)),
            ("tgamma", 3.5, libjq.jq_tgamma(3.5)),
            ("significand", double.Epsilon, libjq.jq_significand(double.Epsilon)),
        ];

        foreach (var (name, input, expected) in cases)
        {
            var result = Find(name).fptr.a1!(jq, libjq.jv_number(input));
            Assert.Equal(
                BitConverter.DoubleToUInt64Bits(expected),
                BitConverter.DoubleToUInt64Bits(result.NumberValue));
            libjq.jv_free(result);
        }
    }

    private static cfunction Find(string name) =>
        Assert.Single(libjq.function_list, function => function.name == name);

    private static IEnumerable<inst> Enumerate(block value)
    {
        for (var instruction = value.first; instruction is not null; instruction = instruction.next)
        {
            yield return instruction;
        }
    }
}
