// `./dotnet.sh run --project tests/backend [-- --filter NAME] [-- --json PATH] [-- --cases PATH]`
//
// Every test is a `[Test]` static method somewhere under `tests/backend/<feature>/`;
// the runner is `Support/MiniTest.cs`.  `--json` writes the fire control's solutions
// in the shape `tests/compare.mjs` reads, which is how the two hosts are compared
// (`run_tests.sh`).

using System.Reflection;

using Mech.Tests;

string? filter = null, json = null;
string? cases = null;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--filter" when i + 1 < args.Length: filter = args[++i]; break;
        case "--json" when i + 1 < args.Length: json = args[++i]; break;
        case "--cases" when i + 1 < args.Length: cases = args[++i]; break;
    }
}
cases ??= FindCases();
Cases.Load(cases);

int rc = MiniTest.Run(Assembly.GetExecutingAssembly(), filter);

if (json is not null)
{
    File.WriteAllText(json, FcsTests.SolutionsJson());
    Console.WriteLine($"solutions written to {json}");
}
return rc;

/// <summary>The shared case table, found from a run anywhere in the tree.</summary>
static string FindCases()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        string candidate = Path.Combine(dir.FullName, "tests", "cases", "fcs.json");
        if (File.Exists(candidate)) return candidate;
        dir = dir.Parent;
    }
    throw new FileNotFoundException("tests/cases/fcs.json is not above " + AppContext.BaseDirectory);
}
