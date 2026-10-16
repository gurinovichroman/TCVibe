using System.Collections.Concurrent;
using TCV.Core.Domain;

namespace TCV.App.Operations;

/// <summary>
/// The destination paths the files of one parallel copy are writing to. Two files must never write
/// one path at once: the free name picked for one («a (1).txt») can be the name of a sibling still
/// on its way, and a case-sensitive server can hold «A.txt» and «a.txt», which are one file on the
/// disk. The second file waits for the first to finish and then meets it as an ordinary conflict,
/// as it would in a sequential copy.
/// </summary>
internal sealed class DestinationClaims
{
    private readonly ConcurrentDictionary<string, TaskCompletionSource> _owners =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The claims of one file: all of them are given up when it is done.</summary>
    public Scope OpenScope() => new(this);

    internal sealed class Scope : IDisposable
    {
        private readonly DestinationClaims _claims;
        private readonly List<KeyValuePair<string, TaskCompletionSource>> _held = [];

        public Scope(DestinationClaims claims) => _claims = claims;

        /// <summary>Takes <paramref name="path"/>, waiting while another file of the copy holds it.</summary>
        public async Task ClaimAsync(VfsPath path, CancellationToken ct)
        {
            while (!TryClaim(path))
            {
                if (_claims._owners.TryGetValue(path.Value, out var owner))
                {
                    await owner.Task.WaitAsync(ct);
                }
            }
        }

        /// <summary>Takes <paramref name="path"/> unless another file of the copy holds it.</summary>
        public bool TryClaim(VfsPath path)
        {
            var claim = new KeyValuePair<string, TaskCompletionSource>(
                path.Value, new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (!_claims._owners.TryAdd(claim.Key, claim.Value))
            {
                return false;
            }

            _held.Add(claim);
            return true;
        }

        /// <summary>Gives up a path taken by <see cref="TryClaim"/> that turned out not to be needed.</summary>
        public void Release(VfsPath path)
        {
            var index = _held.FindIndex(c => string.Equals(c.Key, path.Value, StringComparison.OrdinalIgnoreCase));
            if (index < 0)
            {
                return;
            }

            Free(_held[index]);
            _held.RemoveAt(index);
        }

        public void Dispose()
        {
            foreach (var claim in _held)
            {
                Free(claim);
            }
            _held.Clear();
        }

        private void Free(KeyValuePair<string, TaskCompletionSource> claim)
        {
            _claims._owners.TryRemove(claim);
            claim.Value.TrySetResult();
        }
    }
}
