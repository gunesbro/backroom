using Backroom.EndToEndTests;

// One environment (control plane, two cells and their backing services) for the whole run; tests prepare their own
// data in it. They run one after another (xunit.runner.json): organization placement depends on what exists.
[assembly: AssemblyFixture(typeof(EndToEndEnvironment))]
