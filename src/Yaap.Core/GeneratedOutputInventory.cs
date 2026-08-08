namespace Yaap.Core;

public static class GeneratedOutputInventory
{
    public static async Task<IReadOnlyList<GeneratedOutput>> InspectAsync(
        string rootPath,
        CancellationToken cancellationToken = default)
    {
        if (!Directory.Exists(rootPath))
        {
            return Array.Empty<GeneratedOutput>();
        }

        List<GeneratedOutput> outputs = new();
        foreach (string file in Directory.EnumerateFiles(rootPath, "*", SearchOption.AllDirectories))
        {
            cancellationToken.ThrowIfCancellationRequested();
            FileInfo info = new(file);
            string relativePath = Path.GetRelativePath(rootPath, file).Replace('\\', '/');
            string[] segments = relativePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
            string generator = segments.Length >= 2
                ? segments[^2]
                : "unknown";
            long lineCount = await CountLinesAsync(file, cancellationToken).ConfigureAwait(false);
            outputs.Add(new GeneratedOutput(generator, relativePath, info.Length, lineCount));
        }

        return outputs
            .OrderBy(output => output.GeneratorIdentity, StringComparer.Ordinal)
            .ThenBy(output => output.RelativePath, StringComparer.Ordinal)
            .ToArray();
    }

    private static async Task<long> CountLinesAsync(string path, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[64 * 1024];
        long lines = 0;
        long bytes = 0;
        byte last = 0;
        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            buffer.Length,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        while (true)
        {
            int read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
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
}
