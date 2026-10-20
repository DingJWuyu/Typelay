// Test-owner cleanup shared with the portable ownership regression. Root exit
// is intentionally not an input: only each retained owner's family proof can
// authorize deleting that test's isolated data.
internal static class OrdinaryApplicationCleanup
{
    internal static async Task<bool> RunAsync(IEnumerable<Func<CancellationToken, Task>> familyProofs,
        bool ownershipUnconfirmed, Func<Task> deleteOwnedData, Action<string> failed)
    {
        bool confirmed = !ownershipUnconfirmed;
        foreach (var proof in familyProofs)
        {
            try
            {
                using var exit = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                await proof(exit.Token);
            }
            catch (Exception error) when (error is not OutOfMemoryException)
            {
                confirmed = false;
                failed("ordinary-owned-family-exit-unconfirmed");
            }
        }
        if (!confirmed)
        {
            failed("ordinary-owned-data-preserved");
            return false;
        }
        try
        {
            await deleteOwnedData();
            return true;
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        {
            failed("ordinary-owned-data-cleanup-failed");
            return false;
        }
    }
}
