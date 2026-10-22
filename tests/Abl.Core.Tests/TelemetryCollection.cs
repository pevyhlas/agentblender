using Xunit;

namespace Abl.Core.Tests;

// Fixtures observe process-global ActivitySource/Meter instances. They must measure
// their own operation, not an unrelated concurrent test's measurements.
[CollectionDefinition("op-telemetry", DisableParallelization = true)]
public sealed class TelemetryCollection;
