namespace Yaap.Core;

public static class GeneratedOutputInventory
{
    public static async IAsyncEnumerable<GeneratedOutput> InspectAsync(
        string rootPath,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            yield break;
        }

        EnumerationOptions enumerationOptions = new()
        {
            AttributesToSkip = FileAttributes.ReparsePoint,
            IgnoreInaccessible = false,
            RecurseSubdirectories = true,
            ReturnSpecialDirectories = false,
        };
        foreach (string file in Directory.EnumerateFiles(rootPath, "*", enumerationOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(file);
            string relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string assembly = segments.Length >= 3 ? segments[0] : string.Empty;
            string generator = segments.Length >= 3
                ? segments[1]
                : segments.Length >= 2
                    ? segments[^2]
                : "unknown";
            long lineCount = await CountLinesAsync(file, cancellationToken).ConfigureAwait(false);
            yield return new GeneratedOutput(generator, assembly, relativePath, info.Length, lineCount);
        }
    }

    private static async Task<long> CountLinesAsync(string path, CancellationToken cancellationToken)
    {
        byte[] buffer = System.Buffers.ArrayPool<byte>.Shared.Rent(64 * 1024);
        try
        {
            long lines = 0;
            long bytes = 0;
            byte last = 0;
            await using FileStream stream = new(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            while (true)
            {
                int read = await stream.ReadAsync(
                    buffer.AsMemory(0, 64 * 1024),
                    cancellationToken).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                bytes += read;
                for (int index = 0; index < read; index++)
                {
                    if (buffer[index] == (byte)'\n')
                    {
                        lines++;
                    }
                }

                last = buffer[read - 1];
            }

            if (bytes > 0 && last != (byte)'\n')
            {
                lines++;
            }

            return lines;
        }
        finally
        {
            System.Buffers.ArrayPool<byte>.Shared.Return(buffer);
        }
    }
}
