namespace Dispatch.Web.Tests;

// All tests that need the Kestrel host share ONE WebTestHost (fixed ports) and run sequentially,
// so two hosts never fight over the same ports.
[CollectionDefinition("web")]
public sealed class WebCollection : ICollectionFixture<WebTestHost>;
