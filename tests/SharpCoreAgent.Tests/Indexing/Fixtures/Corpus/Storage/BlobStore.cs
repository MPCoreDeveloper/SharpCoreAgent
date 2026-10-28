namespace Demo.Storage;

/// Writes a blob to a temporary file and renames it into place, so a reader never sees half a payload.
public sealed class BlobStore(string root)
{
    public async Task<string> PutAsync(string name, Stream content, CancellationToken cancellationToken)
    {
        var final = Path.Combine(root, name);
        var temporary = final + ".partial";

        await using (var file = File.Create(temporary))
        {
            await content.CopyToAsync(file, cancellationToken);
            await file.FlushAsync(cancellationToken);
        }

        File.Move(temporary, final, overwrite: true);
        return final;
    }
}
