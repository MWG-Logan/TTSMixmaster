// Classes run in parallel, the methods inside a class in order (what xUnit did by default).
// Classes that hold machine-wide resources opt out with [DoNotParallelize] and run after the rest:
// ProcessRunnerTests (timing-sensitive child processes), SpotifyAuthenticationTests (loopback ports
// and HttpListener prefixes), ReviewRound2Tests (child processes + the named state mutex) and
// AzureAccountTests (the real %LocalAppData%\.IdentityService cache folder).
[assembly: Parallelize(Workers = 0, Scope = ExecutionScope.ClassLevel)]
