using System.Text.Json;
using Microsoft.Data.Sqlite;
using Pinboard.App.Services;

if (args.Length >= 2 && args[0].Equals("--reocr", StringComparison.OrdinalIgnoreCase))
{
    try
    {
        var boardPath = Path.GetFullPath(args[1]);
        var skippedHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var connection = new SqliteConnection($"Data Source={boardPath};Mode=ReadOnly;Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT asset_hash FROM ocr WHERE language='none' AND status='skipped'";
            await using var reader = await command.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                skippedHashes.Add(reader.GetString(0));
            }
        }

        var document = new PinboardDocument(boardPath);
        var snapshot = await document.LoadSnapshotAsync();
        using var scene = JsonDocument.Parse(snapshot.SceneJson);
        var positions = new Dictionary<string, (double X, double Y)>(StringComparer.Ordinal);
        if (scene.RootElement.TryGetProperty("elements", out var elements))
        {
            foreach (var element in elements.EnumerateArray())
            {
                if (!element.TryGetProperty("fileId", out var fileIdNode) || fileIdNode.GetString() is not { } fileId)
                {
                    continue;
                }
                positions[fileId] = (
                    element.TryGetProperty("x", out var x) ? x.GetDouble() : 0,
                    element.TryGetProperty("y", out var y) ? y.GetDouble() : 0);
            }
        }

        var service = new OcrService();
        var completed = 0;
        var failed = 0;
        foreach (var file in snapshot.Files.Where(file => !skippedHashes.Contains(file.Hash)))
        {
            var bytes = ImageProcessor.DecodeDataUrl(file.DataUrl).Bytes;
            positions.TryGetValue(file.FileId, out var location);
            var results = await service.RecognizeAsync(bytes, chinese: true, english: true);
            foreach (var result in results)
            {
                await document.SaveOcrAsync(file.Hash, result.Language, result.Text, result.Status, result.Error, location.X, location.Y);
                if (result.Status == "complete") completed++; else failed++;
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new { Board = boardPath, Completed = completed, Failed = failed }, new JsonSerializerOptions { WriteIndented = true }));
        return failed == 0 ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine(ex);
        return 1;
    }
}

if (args.Length < 2)
{
    Console.Error.WriteLine("用法: Pinboard.Migrate <源 .excalidraw.md/.excalidraw> <目标 .pinboard> [--skip-ocr]");
    Console.Error.WriteLine("      Pinboard.Migrate --reocr <目标 .pinboard>");
    return 2;
}

try
{
    var source = Path.GetFullPath(args[0]);
    var destination = Path.GetFullPath(args[1]);
    var runOcr = !args.Contains("--skip-ocr", StringComparer.OrdinalIgnoreCase);
    var importer = new ObsidianImporter(new ImageProcessor(), new OcrService());
    var report = source.EndsWith(".excalidraw.md", StringComparison.OrdinalIgnoreCase)
        ? await importer.ImportAsync(source, destination, 80, runOcr, runOcr)
        : await importer.ImportStandardAsync(source, destination, 80, runOcr, runOcr);
    Console.WriteLine(JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
    return 0;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}
