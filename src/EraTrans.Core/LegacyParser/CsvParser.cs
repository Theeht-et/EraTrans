using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace EraTrans.Core;



public class CSVParser
{
    List<string> valueList = new List<string>();
    private readonly Dictionary<string, string> contextByValue = new(StringComparer.Ordinal);
    public void ParseFile(string filePath)
    {
        var lines = File.ReadAllLines(filePath, Configs.fileEncoding);
        foreach (var line in lines)
        {
            IEnumerable<string> values = Tools.GetCSVValue(line);
            if (values is null)
            {
                continue;
            }

            foreach (string value in values)
            {
                valueList.Add(value);
                contextByValue.TryAdd(value, line);
            }
        }
    }

    /// <summary>
    /// 在本程序的目录下输出Json
    /// </summary>
    /// <param name="targetFile">完整路径</param>
    /// <param name="relativePath">相对路径</param>
    public void WriteJson(string targetFile, string relativePath)
    {
        var PTJsonObjList = valueList.Distinct().Select((item, index) =>
        {
            var entry = new JObject
            {
                // 键值是相对路径(去除后缀)+ 5位数字ID
                ["key"] = Path.ChangeExtension(relativePath, "") + index.ToString().PadLeft(5, '0'),
                ["original"] = item,
                ["translation"] = "",
                ["stage"] = 0
            };
            AddContext(entry, item);
            return entry;
        });

        if (PTJsonObjList.Count() > 0)
        {
            string jsonContent = JsonConvert.SerializeObject(PTJsonObjList, Formatting.Indented);
            File.WriteAllText(Path.ChangeExtension(targetFile, ".json"), jsonContent);
        }
    }

    public void WriteJson(string targetFile, string relativePath, List<string> referenceList)
    {
        // 条数相等不一定匹配，但不相等肯定不匹配
        if (valueList.Count == referenceList.Count)
        {
            var originObjs = valueList.Distinct().ToList();
            var referenceObjs = referenceList.Distinct().ToList();
            // 二次检查
            if (originObjs.Count == referenceObjs.Count)
            {
                List<JObject> PTJsonObjList = new List<JObject>();
                for (int index = 0; index < originObjs.Count; index++)
                {
                    // 如果对比发现，参考和原版不一致，那么把参考的值填进translation
                    if (originObjs[index] != referenceObjs[index])
                    {
                        var entry = new JObject
                        {
                            // 键值是相对路径(去除后缀)+ 5位数字ID
                            ["key"] = new StringBuilder("变量")
                            .Append(Path.ChangeExtension(relativePath, ""))
                            .Append(index.ToString().PadLeft(5, '0'))
                            .ToString(),
                            ["original"] = originObjs[index],
                            ["translation"] = referenceObjs[index],
                            ["stage"] = 1
                        };
                        AddContext(entry, originObjs[index]);
                        PTJsonObjList.Add(entry);
                    }
                    else
                    {
                        var entry = new JObject
                        {
                            ["key"] = new StringBuilder("变量")
                            .Append(Path.ChangeExtension(relativePath, ""))
                            .Append(index.ToString().PadLeft(5, '0'))
                            .ToString(),
                            ["original"] = originObjs[index],
                            ["translation"] = "",
                            ["stage"] = 0
                        };
                        AddContext(entry, originObjs[index]);
                        PTJsonObjList.Add(entry);
                    }
                }
                if (PTJsonObjList.Count > 0)
                {
                    string jsonContent = JsonConvert.SerializeObject(PTJsonObjList, Formatting.Indented);
                    File.WriteAllText(Path.ChangeExtension(targetFile, ".json"), jsonContent);
                }
                return;
            }
        }
        Console.WriteLine($"【警告】：跳过了填充{relativePath}。");
        // 改用常规模式
        WriteJson(targetFile, relativePath);
    }
    private void AddContext(JObject entry, string original)
    {
        if (Configs.writeContext && contextByValue.TryGetValue(original, out string? context))
        {
            entry["context"] = context;
        }
    }

    /// <summary>
    /// 返回valueList
    /// </summary>
    /// <returns></returns>
    public List<string> GetList()
    {
        return valueList;
    }
}

