using System.Runtime.CompilerServices;

// The integration test project exercises internal pipeline pieces directly where driving them
// through a real server would be racy — the canceled-request handler being the first: winning a
// race against a genuinely aborted socket makes a flaky test, while the handler's contract is
// three deterministic cases. Same policy as GymOS.Platform.Infrastructure's marker.
[assembly: InternalsVisibleTo("GymOS.IntegrationTests")]
