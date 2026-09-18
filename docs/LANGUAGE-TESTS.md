# Language tests

Small, self-contained COR-C# programs that exercise the compiler one feature at
a time, in roughly increasing difficulty so that a failure points at the
feature that broke rather than at "something". Each one prints deterministic
text and returns 0 from `Main` when it succeeds.

## Running

```
bash tests/language/run.sh              # every test
bash tests/language/run.sh strings      # tests whose file name contains "strings"
bash tests/language/run.sh -v hello     # also show what the compiler printed
bash tests/language/run.sh --opt-size   # every test using size-oriented inlining
```

The runner builds `compiler/corc.csproj` (Release) if no `corc.dll` is found,
then compiles each `tests/language/*.cor` with the configured runtime/library
source set and executes the result. Conceptually:

```
corc compile tests/language/<name>.cor -o <tmp>/<name>
<tmp>/<name>
```

with a 10 second timeout, compares stdout and the exit code with the file's
header, prints `PASS`/`FAIL` per test (with a diff on failure) and a summary
line, and exits non-zero if anything failed. It needs only bash, awk, sed,
diff, and `timeout` from coreutils.

Environment variables:

| variable    | meaning                                                            |
|-------------|--------------------------------------------------------------------|
| `CORC`      | command that runs the compiler, e.g. `dotnet path/to/corc.dll`     |
| `CORC_LIBS` | Override the runtime/library source set declared in `tests/language/run.sh` |
| `TIMEOUT`   | seconds a compiled test may run (default 10)                        |
| `KEEP=1`    | keep the temporary build directory, whose path is printed on failure |

## Header format

Every test starts with a comment block the runner reads:

```
// test: <short name>
// expect-exit: 0
// expect-output:
// first expected line of stdout
// second expected line
//
// A free-form description of what the test covers may follow, after the
// bare "//" line that closes the expected-output block.
```

- `sources` (optional) lists extra source files, relative to the repository
  root, compiled with the test, such as helpers in `tests/language/support/`.
  Kernel/driver integration fixtures belong in the OS repository.
- `expect-exit` is the exit code `Main` must return. `nonzero` accepts any
  non-zero code, for tests of runtime failures (a bounds check, an uncaught
  exception) whose exact code is the runtime's choice.
- `expect-output` is optional. When present, stdout must match the listed
  lines exactly. When absent, stdout is not compared -- used together with
  `expect-exit: nonzero` where the runtime's failure message is not part of
  the contract.

The expected-output block consists of the `// ` lines (slash, slash, space)
immediately after `// expect-output:`; it ends at the first line that is not
one of those -- a bare `//`, a blank line, or code. An expected line cannot be
empty, so a test that wants to check an empty line of output should print a
marker instead.

## Adding a test

1. Use a normal C# entry point and standard APIs for the behavior under test.
   Namespaces and using directives follow ordinary C# rules. Existing fixtures
   may use low-level output helpers for isolated code-generation tests.
2. Print the values you are checking rather than only "ok"/"fail": a diff of
   the wrong number is more useful than a count of failures.
3. Record standard expected behavior. Missing parser, compiler or library
   support is a defect to fix; do not replace valid syntax with a workaround
   merely to make a fixture pass. Keep unrelated features out of focused cases.
4. Keep the operands of the interesting computations in variables, not
   literals, so the constant folder cannot do the work at compile time.
5. Number the file so it sorts after related tests (`NN_name.cor`), fill in
   the header, and run `bash tests/language/run.sh name` for focused verification.

## What is covered

| file | covers |
|------|--------|
| 01_hello | printing and a zero exit code |
| 02_int_arith | int operators, precedence, compound assignment, ++/--, wrap on overflow, bitwise ops |
| 03_divrem | signed truncating division and remainder, unsigned 32-bit division, 64-bit division signs |
| 04_narrow_types | byte/sbyte/short/ushort wrap, narrowing casts, promotion to int, byte arrays, char cast |
| 05_shifts | arithmetic vs logical right shift, variable counts, 64-bit shifts below/at/above 32 |
| 06_long_arith | 64-bit add/sub with carry, mixed-sign multiply, division, comparisons, int/long/uint conversions, ulong parse |
| 07_bool_logic | &&/\|\| short-circuit with side effects, !, bool equality, ?: |
| 08_control_flow | if/else, while, do-while, for, nested break, continue, early return |
| 09_foreach | foreach over int/string/long arrays, break/continue, nested, empty, chars of a string |
| 10_switch_int | dense and sparse int switches, shared labels, break inside a loop, long subject |
| 11_switch_string | switch on strings including empty and run-time built values |
| 12_switch_expr | switch expressions: type patterns, guards, constants, `or` patterns; relational `is` patterns |
| 13_strings | concatenation, Length, indexing, ==/</>=, Compare, Substring/IndexOf/Replace/Trim/Pad |
| 14_string_interp | `$"..."` with expressions, escapes, nesting, single evaluation |
| 15_string_tools | string.Join, Split, Int32/Int64 parse, Convert bases, StringBuilder growth |
| 16_arrays | new/initialisers/Length/defaults, aliasing, null elements, jagged, object arrays, Array.IndexOf |
| 17_array_bounds | out-of-range index must fail with a non-zero exit (output not compared) |
| 18_classes | fields, ctors, `: this(...)` chaining, static field, identity, method returning `this` |
| 19_inheritance | abstract/virtual/override, base ctor, dispatch through base refs |
| 20_interfaces | interface dispatch via arrays/List/params, is/as on interfaces, override reached via interface |
| 21_typetests | is/as through base chains and interfaces, declaration patterns, null |
| 22_tostring | ToString overrides via `"" + obj`, default type name, null, string.Join |
| 23_structs | user struct copy semantics, struct arrays, by-value passing, KeyValuePair, List of structs |
| 24_properties | get/set bodies, expression-bodied, auto, init, compound assignment, indexer, static property |
| 25_statics | static initialiser order, static ctor, const/readonly, static counter, instance field initialiser |
| 26_generics_list | List<T> over int/long/string/object: Add/RemoveAt/Insert/IndexOf/Remove/Reverse/ToArray, growth |
| 27_generics_dict | Dictionary over string/int/long keys: set/get/overwrite/TryGetValue/Remove, foreach, growth |
| 28_generics_user | user generic class over int/long/string/nested, two-parameter pair over (long,long), generic stack, generic methods |
| 29_lambdas | closures capturing locals mutated after capture, writing captures, loop captures, `this`, nested |
| 30_delegates_linq | Func/Action parameters and fields, long-returning Func, Where/Select/Any/All/Sum/First/Count |
| 31_exceptions_basic | throw/catch by type, Message, through frames, clause order, base-typed catch, library exceptions |
| 32_exceptions_finally | finally ordering on normal/throw/return/break paths, nested, unwinding through frames |
| 33_exceptions_filter_rethrow | `when` filters, rethrow from a catch, InnerException, filter evaluated once, unnamed catch |
| 34_nullable | int?/long? HasValue/Value/==/??, passing and returning, nullable field |
| 35_refout | ref/out on locals, statics, fields, array elements, doubles, many parameters |
| 36_tuples | tuple returns, names and Item positions, deconstruction, tuples in List, (long,long) |
| 37_checked_overflow | checked add/sub/mul on int and long throw; unchecked wraps |
| 38_floats | double/float arithmetic, comparisons, truncating conversions, promotion, mixing with ints |
| 39_float_math | Math.Sqrt/Abs intrinsics, Pow/Sin/Cos/Exp/Pi/Min/Max/Clamp/Lerp |
| 40_recursion | fib, Ackermann, gcd, factorial, mutual recursion, 10000 frames deep |
| 41_enums | enum values, comparison, switch, casts, in a List, as a field |
| 42_stress_sieve | sieve to 10000 cross-checked with trial division |
| 43_stress_sort | LCG data, insertion sort vs quicksort of 1000 elements, long checksum |
| 44_stress_wordcount | Dictionary word count over 1000 words and a split sentence |
| 45_params_defaults | params arrays, optional parameters, named arguments |
| 46_local_functions | local functions reading/writing enclosing locals, expression-bodied, recursive |
| 47_collections_misc | HashSet add/remove/growth, Queue, set of strings, foreach over a set |
| 48_unsigned | uint/ulong wrap, unsigned div/rem/compare, conversions, 64-bit unsigned multiply, parse |
| 49_enumerable_custom | user IEnumerable<T>/IEnumerator<T> driven by foreach, IEnumerable/IReadOnlyList views of List |
| 50_objinit | object and collection initialisers, init properties, setter bodies during initialisation |
| 51_uncaught_exception | an uncaught exception exits non-zero after running finally (output not compared) |
| 52_null_deref | a field read through null exits non-zero (output not compared) |
| 53_chars | char values and arithmetic, Char predicates, building strings from chars |
| 54_base_call | `base.Method()` from overrides, through two levels |
| 55_dict_deconstruct | deconstructing foreach over a Dictionary, KeyValuePair deconstruction |
| 56_out_var | `out var` / `out int x` declared at the call site |
| 70_tasks_manual | lib/threading.cor without async: TaskCompletionSource, posted continuations, Wait, faults and cancellation, Delay ordering, WhenAll/WhenAny, Yield, Task.Run unwrapping, cancellation tokens, deadlock report |
