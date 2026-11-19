using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace EraTrans.Core;

internal static class PZJson
{
    internal static void WriteErdFileNameDictionary(string targetFile, IReadOnlyList<GameFile> erdFiles)
    {
        var jsonEntries = new List<JObject>(erdFiles.Count);
        for (int i = 0; i < erdFiles.Count; i++)
        {
            jsonEntries.Add(new JObject
            {
                ["key"] = $"文件名{i:D3}",
                ["original"] = Path.GetFileNameWithoutExtension(erdFiles[i].FullPath),
                ["translation"] = "",
                ["stage"] = 0
            });
        }

        Directory.CreateDirectory(Path.GetDirectoryName(targetFile)!);
        string jsonOutput = JsonConvert.SerializeObject(jsonEntries, Formatting.Indented);
        File.WriteAllText(targetFile, jsonOutput);
    }
}