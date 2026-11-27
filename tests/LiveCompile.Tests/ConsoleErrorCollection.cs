namespace LiveCompile.Tests;

// Tests that swap the process-wide Console.Error must not overlap other tests' writes to it.
[CollectionDefinition("ConsoleError", DisableParallelization = true)]
public class ConsoleErrorCollection { }
