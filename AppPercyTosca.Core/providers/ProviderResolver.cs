namespace AppPercyTosca.Core
{
    /// <summary>
    /// Chooses the capture path for a session.
    /// </summary>
    public static class ProviderResolver
    {
        public static GenericProvider ResolveProvider(
            IMobileDriver driver, PercyClient client, Cache<string, object?> sessionCache)
        {
            return AppAutomate.Supports(driver)
                ? new AppAutomate(driver, client, sessionCache)
                : new GenericProvider(driver, client, sessionCache);
        }
    }
}
