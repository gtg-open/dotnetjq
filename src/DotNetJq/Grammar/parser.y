/*
 * Maintained managed grammar ported from jq 1.8.2 src/parser.y.
 * jq-port-upstream-repository: https://github.com/jqlang/jq
 * jq-port-upstream-revision: 34f7186b86743a083a589741b6cea95293524108
 * jq-port-upstream-path: src/parser.y
 * Upstream URL:
 *   https://github.com/jqlang/jq/blob/34f7186b86743a083a589741b6cea95293524108/src/parser.y
 *
 * The token, precedence, nonterminal, production, and action order below is a
 * line-for-line C# translation of the pinned grammar. Semantic actions emit
 * jq's block/inst compiler IR directly. ParserBlockOwner is only the managed
 * replacement for Bison's `%destructor { block_free($$); } <blk>`; it is not
 * an alternate syntax tree or compiler representation.
 */

%output=JqGeneratedParser.g.cs
%using System
%using System.Collections.Generic
%using DotNetJq
%using DotNetJq.Port
%namespace DotNetJq.Port.GeneratedParser
%parsertype JqGeneratedParser
%tokentype JqParserToken
%visibility internal
%partial
%start TopLevel

%YYLTYPE JqParserLocation

%union {
  public jv literal;
  public ParserBlockOwner blk;
}

/* GPPG has no %destructor directive. JqGeneratedParserScanner owns every
 * token <literal>, while ParserBlockOwner owns every <blk>; ParseProgram
 * releases semantic values discarded by recovery or abort. */

%token INVALID_CHARACTER
%token <literal> IDENT
%token <literal> FIELD
%token <literal> BINDING
%token <literal> LITERAL
%token <literal> FORMAT
%token REC ".."
%token SETMOD "%="
%token EQ "=="
%token NEQ "!="
%token DEFINEDOR "//"
%token AS "as"
%token DEF "def"
%token MODULE "module"
%token IMPORT "import"
%token INCLUDE "include"
%token IF "if"
%token THEN "then"
%token ELSE "else"
%token ELSE_IF "elif"
%token REDUCE "reduce"
%token FOREACH "foreach"
%token END "end"
%token AND "and"
%token OR "or"
%token TRY "try"
%token CATCH "catch"
%token LABEL "label"
%token BREAK "break"
%token LOC "$__loc__"
%token SETPIPE "|="
%token SETPLUS "+="
%token SETMINUS "-="
%token SETMULT "*="
%token SETDIV "/="
%token SETDEFINEDOR "//="
%token LESSEQ "<="
%token GREATEREQ ">="
%token ALTERNATION "?//"

%token QQSTRING_START
%token <literal> QQSTRING_TEXT
%token QQSTRING_INTERP_START
%token QQSTRING_INTERP_END
%token QQSTRING_END

/* Instead of raising this, find a way to use precedence to resolve
 * shift-reduce conflicts.
 *
 * jq-port: upstream-%expect 0
 * Stock GPPG has no corresponding directive; the generator guardrail
 * requires zero unresolved conflicts. */

%left FUNCDEF /* jq-port: upstream-%precedence */
%right '|'
%left ','
%right DEFINEDOR
%nonassoc '=' SETPIPE SETPLUS SETMINUS SETMULT SETDIV SETMOD SETDEFINEDOR
%left OR
%left AND
%nonassoc NEQ EQ '<' '>' LESSEQ GREATEREQ
%left '+' '-'
%left '*' '/' '%'
%left NONOPT /* jq-port: upstream-%precedence */
%left '?' '.' '[' FIELD /* jq-port: upstream-%precedence */
%left TRY /* jq-port: upstream-%precedence */
%left CATCH /* jq-port: upstream-%precedence */

%type <blk> TopLevel
%type <blk> Query Expr Term
%type <blk> DictPairs DictPair DictExpr
%type <blk> ElseBody
%type <blk> String QQString
%type <blk> FuncDef FuncDefs
%type <blk> Module Import Imports ImportWhat ImportFrom
%type <blk> Param Params Arg Args
%type <blk> Patterns RepPatterns Pattern ArrayPats ObjPats ObjPat
%type <literal> Keyword
%type <literal> StringStart

%{
/*
 * Ownership adapters keep the action text source-comparable with parser.y.
 * Each block-returning adapter consumes the same arguments as compile.c. The
 * sole borrowed block operation used here is gen_op_bound().
 */

private const opcode INDEX = opcode.INDEX;
private const opcode INDEX_OPT = opcode.INDEX_OPT;
private const opcode INSERT = opcode.INSERT;
private const opcode DUP = opcode.DUP;
private const opcode POP = opcode.POP;
private const opcode EACH = opcode.EACH;
private const opcode EACH_OPT = opcode.EACH_OPT;
private const opcode BACKTRACK = opcode.BACKTRACK;
private const opcode LOADV = opcode.LOADV;
private const opcode LOADVN = opcode.LOADVN;
private const opcode STOREV = opcode.STOREV;
private const opcode DUP2 = opcode.DUP2;

private void FAIL(JqParserLocation location, string message) =>
    ReportDiagnostic(location, message);

private ParserBlockOwner BLOCK(params ParserBlockOwner[] blocks)
{
    var result = libjq.gen_noop();
    foreach (var item in blocks)
    {
        result = libjq.block_join(result, TakeBlock(item));
    }

    return OwnBlock(result);
}

private ParserBlockOwner gen_location(
    JqParserLocation sourceLocation,
    ParserBlockOwner body) =>
    OwnBlock(libjq.gen_location(
        sourceLocation.NativeLocation,
        generatedParserLocations,
        TakeBlock(body)));

private ParserBlockOwner gen_noop() => OwnBlock(libjq.gen_noop());

private bool block_is_noop(ParserBlockOwner value) =>
    libjq.block_is_noop(BorrowBlock(value));

private bool block_is_single(ParserBlockOwner value) =>
    libjq.block_is_single(BorrowBlock(value));

private bool block_is_const(ParserBlockOwner value) =>
    libjq.block_is_const(BorrowBlock(value));

private jv_kind block_const_kind(ParserBlockOwner value) =>
    libjq.block_const_kind(BorrowBlock(value));

private jv block_const(ParserBlockOwner value) =>
    libjq.block_const(BorrowBlock(value));

private void block_free(ParserBlockOwner value) =>
    value.Free();

private ParserBlockOwner block_join(ParserBlockOwner left, ParserBlockOwner right) =>
    OwnBlock(libjq.block_join(TakeBlock(left), TakeBlock(right)));

private ParserBlockOwner block_bind_referenced(
    ParserBlockOwner binder,
    ParserBlockOwner body,
    int bindFlags) =>
    OwnBlock(libjq.block_bind_referenced(
        TakeBlock(binder),
        TakeBlock(body),
        bindFlags));

private ParserBlockOwner gen_op_simple(opcode op) =>
    OwnBlock(libjq.gen_op_simple(op));

private ParserBlockOwner gen_error(jv constant) =>
    OwnBlock(libjq.gen_error(TakeLiteral(constant)));

private ParserBlockOwner gen_const(jv constant) =>
    OwnBlock(libjq.gen_const(TakeLiteral(constant)));

private ParserBlockOwner gen_op_unbound(opcode op, string name) =>
    OwnBlock(libjq.gen_op_unbound(op, name));

private ParserBlockOwner gen_op_bound(opcode op, ParserBlockOwner binder) =>
    OwnBlock(libjq.gen_op_bound(op, BorrowBlock(binder)));

private ParserBlockOwner gen_op_var_fresh(opcode op, string name) =>
    OwnBlock(libjq.gen_op_var_fresh(op, name));

private ParserBlockOwner gen_module(ParserBlockOwner metadata) =>
    OwnBlock(libjq.gen_module(TakeBlock(metadata)));

private ParserBlockOwner gen_import(jv name, jv alias, int isData) =>
    OwnBlock(libjq.gen_import(TakeLiteral(name), TakeLiteral(alias), isData));

private ParserBlockOwner gen_import_meta(
    ParserBlockOwner import,
    ParserBlockOwner metadata) =>
    OwnBlock(libjq.gen_import_meta(TakeBlock(import), TakeBlock(metadata)));

private ParserBlockOwner gen_function(
    string name,
    ParserBlockOwner formals,
    ParserBlockOwner body) =>
    OwnBlock(libjq.gen_function(name, TakeBlock(formals), TakeBlock(body)));

private ParserBlockOwner gen_param_regular(string name) =>
    OwnBlock(libjq.gen_param_regular(name));

private ParserBlockOwner gen_param(string name) =>
    OwnBlock(libjq.gen_param(name));

private ParserBlockOwner gen_lambda(ParserBlockOwner body) =>
    OwnBlock(libjq.gen_lambda(TakeBlock(body)));

private ParserBlockOwner gen_call(string name, ParserBlockOwner arguments) =>
    OwnBlock(libjq.gen_call(name, TakeBlock(arguments)));

private ParserBlockOwner gen_subexp(ParserBlockOwner body) =>
    OwnBlock(libjq.gen_subexp(TakeBlock(body)));

private ParserBlockOwner gen_both(ParserBlockOwner left, ParserBlockOwner right) =>
    OwnBlock(libjq.gen_both(TakeBlock(left), TakeBlock(right)));

private ParserBlockOwner gen_const_object(ParserBlockOwner expression)
{
    // compile.c consumes expression only when it successfully folds it.
    var result = libjq.gen_const_object(BorrowBlock(expression));
    if (!libjq.block_is_noop(result))
    {
        _ = TakeBlock(expression);
    }

    return OwnBlock(result);
}

private ParserBlockOwner gen_collect(ParserBlockOwner expression) =>
    OwnBlock(libjq.gen_collect(TakeBlock(expression)));

private ParserBlockOwner gen_reduce(
    ParserBlockOwner source,
    ParserBlockOwner matcher,
    ParserBlockOwner initial,
    ParserBlockOwner body) =>
    OwnBlock(libjq.gen_reduce(
        TakeBlock(source),
        TakeBlock(matcher),
        TakeBlock(initial),
        TakeBlock(body)));

private ParserBlockOwner gen_foreach(
    ParserBlockOwner source,
    ParserBlockOwner matcher,
    ParserBlockOwner initial,
    ParserBlockOwner update,
    ParserBlockOwner extract) =>
    OwnBlock(libjq.gen_foreach(
        TakeBlock(source),
        TakeBlock(matcher),
        TakeBlock(initial),
        TakeBlock(update),
        TakeBlock(extract)));

private ParserBlockOwner gen_definedor(ParserBlockOwner left, ParserBlockOwner right) =>
    OwnBlock(libjq.gen_definedor(TakeBlock(left), TakeBlock(right)));

private ParserBlockOwner gen_and(ParserBlockOwner left, ParserBlockOwner right) =>
    OwnBlock(libjq.gen_and(TakeBlock(left), TakeBlock(right)));

private ParserBlockOwner gen_or(ParserBlockOwner left, ParserBlockOwner right) =>
    OwnBlock(libjq.gen_or(TakeBlock(left), TakeBlock(right)));

private ParserBlockOwner gen_dictpair(ParserBlockOwner key, ParserBlockOwner value) =>
    OwnBlock(libjq.gen_dictpair(TakeBlock(key), TakeBlock(value)));

private ParserBlockOwner gen_array_matcher(
    ParserBlockOwner left,
    ParserBlockOwner current) =>
    OwnBlock(libjq.gen_array_matcher(TakeBlock(left), TakeBlock(current)));

private ParserBlockOwner gen_object_matcher(
    ParserBlockOwner name,
    ParserBlockOwner current) =>
    OwnBlock(libjq.gen_object_matcher(TakeBlock(name), TakeBlock(current)));

private ParserBlockOwner gen_destructure(
    ParserBlockOwner value,
    ParserBlockOwner matcher,
    ParserBlockOwner body) =>
    OwnBlock(libjq.gen_destructure(
        TakeBlock(value),
        TakeBlock(matcher),
        TakeBlock(body)));

private ParserBlockOwner gen_destructure_alt(ParserBlockOwner matcher) =>
    OwnBlock(libjq.gen_destructure_alt(TakeBlock(matcher)));

private ParserBlockOwner gen_cond(
    ParserBlockOwner condition,
    ParserBlockOwner ifTrue,
    ParserBlockOwner ifFalse) =>
    OwnBlock(libjq.gen_cond(
        TakeBlock(condition),
        TakeBlock(ifTrue),
        TakeBlock(ifFalse)));

private ParserBlockOwner gen_try(ParserBlockOwner expression, ParserBlockOwner handler) =>
    OwnBlock(libjq.gen_try(TakeBlock(expression), TakeBlock(handler)));

private ParserBlockOwner gen_label(string label, ParserBlockOwner expression) =>
    OwnBlock(libjq.gen_label(label, TakeBlock(expression)));

/* Returns a string message if the block is a constant that is not valid as
 * an object key. */
private static jv check_object_key(ParserBlockOwner key)
{
    if (libjq.block_is_const(BorrowBlock(key)) &&
        libjq.block_const_kind(BorrowBlock(key)) != jv_kind.JV_KIND_STRING)
    {
        var value = libjq.block_const(BorrowBlock(key));
        return libjq.jv_string(
            $"Cannot use {libjq.jv_kind_name(value.Kind)} " +
            $"({libjq.jv_dump_string_trunc(value, 30)}) as object key");
    }

    return libjq.jv_invalid();
}

private ParserBlockOwner gen_index(ParserBlockOwner obj, ParserBlockOwner key) =>
    BLOCK(gen_subexp(key), obj, gen_op_simple(INDEX));

private ParserBlockOwner gen_index_opt(ParserBlockOwner obj, ParserBlockOwner key) =>
    BLOCK(gen_subexp(key), obj, gen_op_simple(INDEX_OPT));

private ParserBlockOwner gen_slice_index(
    ParserBlockOwner obj,
    ParserBlockOwner start,
    ParserBlockOwner end,
    opcode indexOp)
{
    var key = BLOCK(
        gen_subexp(gen_const(libjq.jv_object())),
        gen_subexp(gen_const(libjq.jv_string("start"))),
        gen_subexp(start),
        gen_op_simple(INSERT),
        gen_subexp(gen_const(libjq.jv_string("end"))),
        gen_subexp(end),
        gen_op_simple(INSERT));
    return BLOCK(key, obj, gen_op_simple(indexOp));
}

private ParserBlockOwner constant_fold(
    ParserBlockOwner left,
    ParserBlockOwner right,
    int op)
{
    if (!block_is_single(left) || !block_is_const(left) ||
        !block_is_single(right) || !block_is_const(right))
    {
        return gen_noop();
    }

    var leftValue = block_const(left);
    block_free(left);
    var rightValue = block_const(right);
    block_free(right);

    var result = op switch
    {
        '+' => libjq.binop_plus(leftValue, rightValue),
        '-' => libjq.binop_minus(leftValue, rightValue),
        '*' => libjq.binop_multiply(leftValue, rightValue),
        '/' => libjq.binop_divide(leftValue, rightValue),
        '%' => libjq.binop_mod(leftValue, rightValue),
        (int)JqParserToken.EQ => libjq.binop_equal(leftValue, rightValue),
        (int)JqParserToken.NEQ => libjq.binop_notequal(leftValue, rightValue),
        '<' => libjq.binop_less(leftValue, rightValue),
        '>' => libjq.binop_greater(leftValue, rightValue),
        (int)JqParserToken.LESSEQ => libjq.binop_lesseq(leftValue, rightValue),
        (int)JqParserToken.GREATEREQ => libjq.binop_greatereq(leftValue, rightValue),
        _ => throw new InvalidOperationException("Unknown parser binary operator."),
    };

    return libjq.jv_is_valid(result)
        ? gen_const(result)
        : gen_error(libjq.jv_invalid_get_msg(result));
}

private ParserBlockOwner gen_binop(
    ParserBlockOwner left,
    ParserBlockOwner right,
    int op)
{
    var folded = constant_fold(left, right, op);
    if (!block_is_noop(folded))
    {
        return folded;
    }

    block_free(folded);
    var functionName = op switch
    {
        '+' => "_plus",
        '-' => "_minus",
        '*' => "_multiply",
        '/' => "_divide",
        '%' => "_mod",
        (int)JqParserToken.EQ => "_equal",
        (int)JqParserToken.NEQ => "_notequal",
        '<' => "_less",
        '>' => "_greater",
        (int)JqParserToken.LESSEQ => "_lesseq",
        (int)JqParserToken.GREATEREQ => "_greatereq",
        _ => throw new InvalidOperationException("Unknown parser binary operator."),
    };

    return gen_call(functionName, BLOCK(gen_lambda(left), gen_lambda(right)));
}

private ParserBlockOwner gen_format(ParserBlockOwner expression, jv format) =>
    BLOCK(expression, gen_call("format", gen_lambda(gen_const(format))));

private ParserBlockOwner gen_definedor_assign(
    ParserBlockOwner target,
    ParserBlockOwner value)
{
    var temporary = gen_op_var_fresh(STOREV, "tmp");
    return BLOCK(
        gen_op_simple(DUP),
        value,
        temporary,
        gen_call(
            "_modify",
            BLOCK(
                gen_lambda(target),
                gen_lambda(gen_definedor(
                    gen_noop(),
                    gen_op_bound(LOADV, temporary))))));
}

private ParserBlockOwner gen_update(
    ParserBlockOwner target,
    ParserBlockOwner value,
    int operatorType)
{
    var temporary = gen_op_var_fresh(STOREV, "tmp");
    return BLOCK(
        gen_op_simple(DUP),
        value,
        temporary,
        gen_call(
            "_modify",
            BLOCK(
                gen_lambda(target),
                gen_lambda(gen_binop(
                    gen_noop(),
                    gen_op_bound(LOADV, temporary),
                    operatorType)))));
}

private ParserBlockOwner gen_loc_object(JqParserLocation sourceLocation)
{
    var value = libjq.jv_object();
    value = libjq.jv_object_set(
        value,
        libjq.jv_string("file"),
        libjq.jv_copy(generatedParserLocations.fname));
    value = libjq.jv_object_set(
        value,
        libjq.jv_string("line"),
        libjq.jv_number(
            libjq.locfile_get_line(
                generatedParserLocations,
                sourceLocation.StartByte) +
            generatedParserSourceLineOffset +
            1));
    return gen_const(value);
}

/* GPPG rejects Bison's typed $<literal>0 reference. Keep the same semantic
 * value on an explicit stack: nested interpolated strings push/pop around the
 * inner Query, so the top is exactly parser.y's surrounding StringStart. */
private jv BeginString(jv format)
{
    generatedParserStringFormats.Push(format);
    return format;
}

private jv CurrentStringFormat() =>
    libjq.jv_copy(generatedParserStringFormats.Peek());

private void EndString(jv format)
{
    var active = generatedParserStringFormats.Pop();
    if (!ReferenceEquals(active.Value, format.Value))
    {
        throw new InvalidOperationException("Interpolated-string format stack is unbalanced.");
    }

    FreeLiteral(format);
}

%}

%%
TopLevel:
  Module Imports Query {
  $$ = BLOCK($1, $2, gen_op_simple(opcode.TOP), $3);
} |
  Module Imports FuncDefs {
  $$ = BLOCK($1, $2, $3);
};

Module:
/* jq-port: upstream-%empty */ {
  $$ = gen_noop();
} |
"module" Query ';' {
  if (!block_is_const($2)) {
    FAIL(@2, "Module metadata must be constant");
    $$ = gen_noop();
    block_free($2);
  } else if (block_const_kind($2) != jv_kind.JV_KIND_OBJECT) {
    FAIL(@2, "Module metadata must be an object");
    $$ = gen_noop();
    block_free($2);
  } else {
    $$ = gen_module($2);
  }
};

Imports:
/* jq-port: upstream-%empty */ {
  $$ = gen_noop();
} |
  Import Imports {
  $$ = BLOCK($1, $2);
};

FuncDefs:
/* jq-port: upstream-%empty */ {
  $$ = gen_noop();
} |
  FuncDef FuncDefs {
  $$ = block_join($1, $2);
};

Query:
  FuncDef Query %prec FUNCDEF {
  $$ = block_bind_referenced($1, $2, libjq.OP_IS_CALL_PSEUDO);
} |
  Expr "as" Patterns '|' Query {
  $$ = gen_destructure($1, $3, $5);
} |
"label" BINDING '|' Query {
  var value = libjq.jv_string("*label-" + libjq.jv_string_value($2));
  $$ = gen_location(@$, gen_label(libjq.jv_string_value(value), $4));
  FreeLiteral($2);
  libjq.jv_free(value);
} |
  Query '|' Query {
  $$ = block_join($1, $3);
} |
  Query ',' Query {
  $$ = gen_both($1, $3);
} |
  Expr {
  $$ = $1;
};

Expr:
  Expr "//" Expr {
  $$ = gen_definedor($1, $3);
} |
  Expr '=' Expr {
  $$ = gen_call("_assign", BLOCK(gen_lambda($1), gen_lambda($3)));
} |
  Expr "or" Expr {
  $$ = gen_or($1, $3);
} |
  Expr "and" Expr {
  $$ = gen_and($1, $3);
} |
  Expr "//=" Expr {
  $$ = gen_definedor_assign($1, $3);
} |
  Expr "|=" Expr {
  $$ = gen_call("_modify", BLOCK(gen_lambda($1), gen_lambda($3)));
} |
  Expr '+' Expr {
  $$ = gen_binop($1, $3, '+');
} |
  Expr "+=" Expr {
  $$ = gen_update($1, $3, '+');
} |
  Expr '-' Expr {
  $$ = gen_binop($1, $3, '-');
} |
  Expr "-=" Expr {
  $$ = gen_update($1, $3, '-');
} |
  Expr '*' Expr {
  $$ = gen_binop($1, $3, '*');
} |
  Expr "*=" Expr {
  $$ = gen_update($1, $3, '*');
} |
  Expr '/' Expr {
  $$ = gen_binop($1, $3, '/');
} |
  Expr '%' Expr {
  $$ = gen_binop($1, $3, '%');
} |
  Expr "/=" Expr {
  $$ = gen_update($1, $3, '/');
} |
  Expr SETMOD Expr {
  $$ = gen_update($1, $3, '%');
} |
  Expr "==" Expr {
  $$ = gen_binop($1, $3, (int)JqParserToken.EQ);
} |
  Expr "!=" Expr {
  $$ = gen_binop($1, $3, (int)JqParserToken.NEQ);
} |
  Expr '<' Expr {
  $$ = gen_binop($1, $3, '<');
} |
  Expr '>' Expr {
  $$ = gen_binop($1, $3, '>');
} |
  Expr "<=" Expr {
  $$ = gen_binop($1, $3, (int)JqParserToken.LESSEQ);
} |
  Expr ">=" Expr {
  $$ = gen_binop($1, $3, (int)JqParserToken.GREATEREQ);
} |
  Term %prec NONOPT {
  $$ = $1;
};

Import:
  ImportWhat ';' {
  $$ = $1;
} |
  ImportWhat Query ';' {
  if (!block_is_const($2)) {
    FAIL(@2, "Module metadata must be constant");
    $$ = gen_noop();
    block_free($1);
    block_free($2);
  } else if (block_const_kind($2) != jv_kind.JV_KIND_OBJECT) {
    FAIL(@2, "Module metadata must be an object");
    $$ = gen_noop();
    block_free($1);
    block_free($2);
  } else {
    $$ = gen_import_meta($1, $2);
  }
};

ImportWhat:
"import" ImportFrom "as" BINDING {
  $$ = gen_import(block_const($2), $4, 1);
  block_free($2);
} |
"import" ImportFrom "as" IDENT {
  $$ = gen_import(block_const($2), $4, 0);
  block_free($2);
} |
"include" ImportFrom {
  $$ = gen_import(block_const($2), libjq.jv_invalid(), 0);
  block_free($2);
};

ImportFrom:
  String {
  if (!block_is_const($1)) {
    FAIL(@1, "Import path must be constant");
    $$ = gen_const(libjq.jv_string(""));
    block_free($1);
  } else {
    $$ = $1;
  }
};

FuncDef:
"def" IDENT ':' Query ';' {
  $$ = gen_function(libjq.jv_string_value($2), gen_noop(), $4);
  FreeLiteral($2);
} |

"def" IDENT '(' Params ')' ':' Query ';' {
  $$ = gen_function(libjq.jv_string_value($2), $4, $7);
  FreeLiteral($2);
};

Params:
  Param {
  $$ = $1;
} |
  Params ';' Param {
  $$ = BLOCK($1, $3);
};

Param:
  BINDING {
  $$ = gen_param_regular(libjq.jv_string_value($1));
  FreeLiteral($1);
} |
  IDENT {
  $$ = gen_param(libjq.jv_string_value($1));
  FreeLiteral($1);
};

StringStart:
  FORMAT QQSTRING_START {
  $$ = BeginString($1);
} |
  QQSTRING_START {
  $$ = BeginString(OwnParserLiteral(libjq.jv_string("text")));
};

String:
  StringStart QQString QQSTRING_END {
  $$ = $2;
  EndString($1);
};

QQString:
/* jq-port: upstream-%empty */ {
  $$ = gen_const(libjq.jv_string(""));
} |
  QQString QQSTRING_TEXT {
  $$ = gen_binop($1, gen_const($2), '+');
} |
  QQString QQSTRING_INTERP_START Query QQSTRING_INTERP_END {
  $$ = gen_binop($1, gen_format($3, CurrentStringFormat()), '+');
};

ElseBody:
"elif" Query "then" Query ElseBody {
  $$ = gen_cond($2, $4, $5);
} |
"else" Query "end" {
  $$ = $2;
} |
"end" {
  $$ = gen_noop();
};

Term:
'.' {
  $$ = gen_noop();
} |
  REC {
  $$ = gen_call("recurse", gen_noop());
} |
  BREAK BINDING {
  var value = libjq.jv_string("*label-" + libjq.jv_string_value($2));
  $$ = gen_location(@$, BLOCK(
      gen_op_unbound(LOADV, libjq.jv_string_value(value)),
      gen_call("error", gen_noop())));
  libjq.jv_free(value);
  FreeLiteral($2);
} |
  BREAK error {
  FAIL(@$, "break requires a label to break to");
  $$ = gen_noop();
} |
  Term FIELD '?' {
  $$ = gen_index_opt($1, gen_const($2));
} |
  FIELD '?' {
  $$ = gen_index_opt(gen_noop(), gen_const($1));
} |
  Term '.' String '?' {
  $$ = gen_index_opt($1, $3);
} |
'.' String '?' {
  $$ = gen_index_opt(gen_noop(), $2);
} |
  Term FIELD %prec NONOPT {
  $$ = gen_index($1, gen_const($2));
} |
  FIELD %prec NONOPT {
  $$ = gen_index(gen_noop(), gen_const($1));
} |
  Term '.' String %prec NONOPT {
  $$ = gen_index($1, $3);
} |
'.' String %prec NONOPT {
  $$ = gen_index(gen_noop(), $2);
} |
'.' error {
  FAIL(@$, "try .[\"field\"] instead of .field for unusually named fields");
  $$ = gen_noop();
} |
'.' IDENT error {
  FreeLiteral($2);
  FAIL(@$, "try .[\"field\"] instead of .field for unusually named fields");
  $$ = gen_noop();
} |
/* FIXME: string literals */
  Term '[' Query ']' '?' {
  $$ = gen_index_opt($1, $3);
} |
  Term '[' Query ']' %prec NONOPT {
  $$ = gen_index($1, $3);
} |
  Term '.' '[' Query ']' '?' {
  $$ = gen_index_opt($1, $4);
} |
  Term '.' '[' Query ']' %prec NONOPT {
  $$ = gen_index($1, $4);
} |
  Term '[' ']' '?' {
  $$ = block_join($1, gen_op_simple(EACH_OPT));
} |
  Term '[' ']' %prec NONOPT {
  $$ = block_join($1, gen_op_simple(EACH));
} |
  Term '.' '[' ']' '?' {
  $$ = block_join($1, gen_op_simple(EACH_OPT));
} |
  Term '.' '[' ']' %prec NONOPT {
  $$ = block_join($1, gen_op_simple(EACH));
} |
  Term '[' Query ':' Query ']' '?' {
  $$ = gen_slice_index($1, $3, $5, INDEX_OPT);
} |
  Term '[' Query ':' ']' '?' {
  $$ = gen_slice_index($1, $3, gen_const(libjq.jv_null()), INDEX_OPT);
} |
  Term '[' ':' Query ']' '?' {
  $$ = gen_slice_index($1, gen_const(libjq.jv_null()), $4, INDEX_OPT);
} |
  Term '[' Query ':' Query ']' %prec NONOPT {
  $$ = gen_slice_index($1, $3, $5, INDEX);
} |
  Term '[' Query ':' ']' %prec NONOPT {
  $$ = gen_slice_index($1, $3, gen_const(libjq.jv_null()), INDEX);
} |
  Term '[' ':' Query ']' %prec NONOPT {
  $$ = gen_slice_index($1, gen_const(libjq.jv_null()), $4, INDEX);
} |
  Term '?' {
  $$ = gen_try($1, gen_op_simple(BACKTRACK));
} |
  LITERAL {
  $$ = gen_const($1);
} |
  String {
  $$ = $1;
} |
  FORMAT {
  $$ = gen_format(gen_noop(), $1);
} |
'-' Term {
  $$ = BLOCK($2, gen_call("_negate", gen_noop()));
} |
'(' Query ')' {
  $$ = $2;
} |
'[' Query ']' {
  $$ = gen_collect($2);
} |
'[' ']' {
  $$ = gen_const(libjq.jv_array());
} |
'{' DictPairs '}' {
  var obj = gen_const_object($2);
  if (!block_is_noop(obj))
    $$ = obj;
  else {
    block_free(obj);
    $$ = BLOCK(gen_subexp(gen_const(libjq.jv_object())), $2, gen_op_simple(POP));
  }
} |
"reduce" Expr "as" Patterns '(' Query ';' Query ')' {
  $$ = gen_reduce($2, $4, $6, $8);
} |
"foreach" Expr "as" Patterns '(' Query ';' Query ';' Query ')' {
  $$ = gen_foreach($2, $4, $6, $8, $10);
} |
"foreach" Expr "as" Patterns '(' Query ';' Query ')' {
  $$ = gen_foreach($2, $4, $6, $8, gen_noop());
} |
"if" Query "then" Query ElseBody {
  $$ = gen_cond($2, $4, $5);
} |
"if" Query "then" error {
  FAIL(@$, "Possibly unterminated 'if' statement");
  $$ = $2;
} |
"try" Expr "catch" Expr {
  $$ = gen_try($2, $4);
} |
"try" Expr "catch" error {
  FAIL(@$, "Possibly unterminated 'try' statement");
  $$ = $2;
} |
"try" Expr {
  $$ = gen_try($2, gen_op_simple(BACKTRACK));
} |
/*
 * This `$$$$varname` hack is strictly private to jq builtins. DO NOT USE!!
 *
 * This is used in `_modify`, in src/builtin.jq, to avoid holding on to a
 * reference to `.`.
 *
 * We could just have the compiler emit bytecode for `_modify` so it can use
 * LOADVN w/o needing jq syntax for LOADVN.
 *
 * This syntax, `$$$$varname`, violates referential transparency: it has
 * side-effects that are surprising.
 *
 * DO NOT USE!! I will break your jq code if you do use this outside
 * src/builtin.jq.
 */
'$' '$' '$' BINDING {
  $$ = gen_location(@$, gen_op_unbound(LOADVN, libjq.jv_string_value($4)));
  FreeLiteral($4);
} |
  BINDING {
  $$ = gen_location(@$, gen_op_unbound(LOADV, libjq.jv_string_value($1)));
  FreeLiteral($1);
} |
"$__loc__" {
  $$ = gen_loc_object(@$);
} |
  IDENT {
  var name = libjq.jv_string_value($1);
  if (name == "false")
    $$ = gen_const(libjq.jv_false());
  else if (name == "true")
    $$ = gen_const(libjq.jv_true());
  else if (name == "null")
    $$ = gen_const(libjq.jv_null());
  else
    $$ = gen_location(@$, gen_call(name, gen_noop()));
  FreeLiteral($1);
} |
  IDENT '(' Args ')' {
  $$ = gen_call(libjq.jv_string_value($1), $3);
  $$ = gen_location(@1, $$);
  FreeLiteral($1);
} |
'(' error ')' { $$ = gen_noop(); } |
'[' error ']' { $$ = gen_noop(); } |
  Term '[' error ']' { $$ = $1; } |
'{' error '}' { $$ = gen_noop(); }
;

Args:
  Arg {
  $$ = $1;
} |
  Args ';' Arg {
  $$ = BLOCK($1, $3);
};

Arg:
  Query {
  $$ = gen_lambda($1);
};

RepPatterns:
  RepPatterns "?//" Pattern {
  $$ = BLOCK($1, gen_destructure_alt($3));
} |
  Pattern {
  $$ = gen_destructure_alt($1);
};

Patterns:
  RepPatterns "?//" Pattern {
  $$ = BLOCK($1, $3);
} |
  Pattern {
  $$ = $1;
};

Pattern:
  BINDING {
  $$ = gen_op_unbound(STOREV, libjq.jv_string_value($1));
  FreeLiteral($1);
} |
'[' ArrayPats ']' {
  $$ = BLOCK($2, gen_op_simple(POP));
} |
'{' ObjPats '}' {
  $$ = BLOCK($2, gen_op_simple(POP));
};

ArrayPats:
  Pattern {
  $$ = gen_array_matcher(gen_noop(), $1);
} |
  ArrayPats ',' Pattern {
  $$ = gen_array_matcher($1, $3);
};

ObjPats:
  ObjPat {
  $$ = $1;
} |
  ObjPats ',' ObjPat {
  $$ = BLOCK($1, $3);
};

ObjPat:
  BINDING {
  $$ = gen_object_matcher(
      gen_const($1),
      gen_op_unbound(STOREV, libjq.jv_string_value($1)));
} |
  BINDING ':' Pattern {
  $$ = gen_object_matcher(
      gen_const($1),
      BLOCK(
          gen_op_simple(DUP),
          gen_op_unbound(STOREV, libjq.jv_string_value($1)),
          $3));
} |
  IDENT ':' Pattern {
  $$ = gen_object_matcher(gen_const($1), $3);
} |
  Keyword ':' Pattern {
  $$ = gen_object_matcher(gen_const($1), $3);
} |
  String ':' Pattern {
  $$ = gen_object_matcher($1, $3);
} |
'(' Query ')' ':' Pattern {
  var message = check_object_key($2);
  if (libjq.jv_is_valid(message)) {
    FAIL(@2, libjq.jv_string_value(message));
  }
  libjq.jv_free(message);
  $$ = gen_object_matcher($2, $5);
} |
  error ':' Pattern {
  FAIL(
      ObjectEntryRecoveryLocation(@$),
      "May need parentheses around object key expression");
  $$ = $3;
};

Keyword:
"as" {
  $$ = OwnParserLiteral(libjq.jv_string("as"));
} |
"def" {
  $$ = OwnParserLiteral(libjq.jv_string("def"));
} |
"module" {
  $$ = OwnParserLiteral(libjq.jv_string("module"));
} |
"import" {
  $$ = OwnParserLiteral(libjq.jv_string("import"));
} |
"include" {
  $$ = OwnParserLiteral(libjq.jv_string("include"));
} |
"if" {
  $$ = OwnParserLiteral(libjq.jv_string("if"));
} |
"then" {
  $$ = OwnParserLiteral(libjq.jv_string("then"));
} |
"else" {
  $$ = OwnParserLiteral(libjq.jv_string("else"));
} |
"elif" {
  $$ = OwnParserLiteral(libjq.jv_string("elif"));
} |
"reduce" {
  $$ = OwnParserLiteral(libjq.jv_string("reduce"));
} |
"foreach" {
  $$ = OwnParserLiteral(libjq.jv_string("foreach"));
} |
"end" {
  $$ = OwnParserLiteral(libjq.jv_string("end"));
} |
"and" {
  $$ = OwnParserLiteral(libjq.jv_string("and"));
} |
"or" {
  $$ = OwnParserLiteral(libjq.jv_string("or"));
} |
"try" {
  $$ = OwnParserLiteral(libjq.jv_string("try"));
} |
"catch" {
  $$ = OwnParserLiteral(libjq.jv_string("catch"));
} |
"label" {
  $$ = OwnParserLiteral(libjq.jv_string("label"));
} |
"break" {
  $$ = OwnParserLiteral(libjq.jv_string("break"));
};

DictPairs:
/* jq-port: upstream-%empty */ {
  $$ = gen_noop();
} |
  DictPair {
  $$ = $1;
} |
  DictPair ',' DictPairs {
  $$ = block_join($1, $3);
};

DictPair:
  IDENT ':' DictExpr {
  $$ = gen_dictpair(gen_const($1), $3);
} |
  Keyword ':' DictExpr {
  $$ = gen_dictpair(gen_const($1), $3);
} |
  String ':' DictExpr {
  $$ = gen_dictpair($1, $3);
} |
  String {
  $$ = gen_dictpair(
      $1,
      BLOCK(
          gen_op_simple(POP),
          gen_op_simple(DUP2),
          gen_op_simple(DUP2),
          gen_op_simple(INDEX)));
} |
  BINDING ':' DictExpr {
  $$ = gen_dictpair(
      gen_location(@$, gen_op_unbound(LOADV, libjq.jv_string_value($1))),
      $3);
  FreeLiteral($1);
} |
  BINDING {
  $$ = gen_dictpair(
      gen_const($1),
      gen_location(@$, gen_op_unbound(LOADV, libjq.jv_string_value($1))));
} |
  IDENT {
  $$ = gen_dictpair(
      gen_const(libjq.jv_copy($1)),
      gen_index(gen_noop(), gen_const($1)));
} |
"$__loc__" {
  $$ = gen_dictpair(
      gen_const(libjq.jv_string("__loc__")),
      gen_loc_object(@$));
} |
  Keyword {
  $$ = gen_dictpair(
      gen_const(libjq.jv_copy($1)),
      gen_index(gen_noop(), gen_const($1)));
} |
'(' Query ')' ':' DictExpr {
  var message = check_object_key($2);
  if (libjq.jv_is_valid(message)) {
    FAIL(@2, libjq.jv_string_value(message));
  }
  libjq.jv_free(message);
  $$ = gen_dictpair($2, $5);
} |
  error ':' DictExpr {
  FAIL(
      ObjectEntryRecoveryLocation(@1),
      "May need parentheses around object key expression");
  $$ = $3;
};

DictExpr:
  DictExpr '|' DictExpr {
  $$ = block_join($1, $3);
} |
  Expr {
  $$ = $1;
};
%%
