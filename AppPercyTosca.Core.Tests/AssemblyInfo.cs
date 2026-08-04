using Xunit;

// The Core carries process-wide state by design — Env's build/session fields, Utils.LogSink,
// DeviceRegistry's memoized table, and the PERCY_* environment variables. Running test classes in
// parallel would let one class observe another's setup, so collection parallelism is off and every
// class that touches that state derives from CoreTestBase to reset it.
[assembly: CollectionBehavior(DisableTestParallelization = true)]
