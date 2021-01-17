using HtmlAgilityPack;
using MongoDB.Driver;
using Serilog;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using WordPressPCL;
using WordPressPCL.Models;

namespace BlogTransferApp
{
    internal class Program
    {
        private const string baseOutputDir = @"C:\develop\GitHub\personal-blog\codewrecks\content\post";

        private static async Task Main(string[] args)
        {
            Log.Logger = new LoggerConfiguration()
                .WriteTo.Console()
                .WriteTo.File("logs\\log.txt", rollingInterval: RollingInterval.Day, restrictedToMinimumLevel: Serilog.Events.LogEventLevel.Error)
                .CreateLogger();

            var client = new WordPressClient("https://www.codewrecks.com/blog/wp-json/");

            var mongoUrl = new MongoUrl("mongodb://admin:CiaoMondo@localhost/codewrecks?authSource=admin");
            var mongoClient = new MongoClient(mongoUrl);
            var db = mongoClient.GetDatabase(mongoUrl.DatabaseName);
            var postsCollection = db.GetCollection<Post>("posts");
            var tagsCollection = db.GetCollection<WordPressPCL.Models.Tag>("tags");
            var categoriesCollection = db.GetCollection<Category>("categories");

            // Posts
            bool shouldLoadPosts = true;
            var postCount = postsCollection.AsQueryable().Count();
            if (postCount > 0)
            {
                Console.Write("Do you want to Re-Load all post into mongodb?");
                var answer = Console.ReadKey();
                if (!answer.Equals('y'))
                {
                    shouldLoadPosts = false;
                }
            }

            if (shouldLoadPosts)
            {
                Console.WriteLine("reading everything from blog");
                var posts = await client.Posts.GetAll(false, true);
                var tags = await client.Tags.GetAll();
                var categories = await client.Categories.GetAll();

                db.DropCollection("posts");
                db.DropCollection("tags");
                db.DropCollection("categories");

                postsCollection.InsertMany(posts);
                tagsCollection.InsertMany(tags);
                categoriesCollection.InsertMany(categories);
            }
            var orderedPosts = postsCollection.AsQueryable()
                .OrderByDescending(p => p.Date)
                .ToList();

            var allCategories = categoriesCollection.AsQueryable()
                .ToDictionary(c => c.Id);

            var allTags = tagsCollection.AsQueryable()
                .ToDictionary(t => t.Id);

            var allTagOnPosts = orderedPosts
                .SelectMany(p => p.Tags)
                .GroupBy(p => p)
                .Select(g => (allTags[g.Key].Name, Count: g.Count()))
                .OrderBy(t => t.Item2)
                .ToList();

            var allMeaningfulTags = allTagOnPosts
                .Where(t => t.Count > 1)
                .Select(t => t.Name);

            HashSet<string> allowedTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var allowedTag in allMeaningfulTags)
            {
                allowedTags.Add(allowedTag);
            }

            var converter = new ReverseMarkdown.Converter();

            Int32 index = 0;
            StringBuilder redirectRules = new StringBuilder();
            foreach (var post in orderedPosts)
            {
                //if (post.Id != 2965)
                //{
                //    continue;
                //}

                index++;
                try
                {
                    var doc = new HtmlDocument();
                    doc.LoadHtml(PreParsePostContent(post));

                    StringBuilder sb = new StringBuilder(post.Content.Rendered.Length);

                    var postCategories = string.Join(",", post
                        .Categories
                        .Select(c => Sanitize($"\"{allCategories[c].Name}\""))
                        .Distinct());

                    if (string.IsNullOrEmpty(postCategories))
                    {
                        postCategories = "General";
                    }

                    var allTagEntries = post
                        .Tags
                        .Where(t => allowedTags.Contains(allTags[t].Name))
                        .Select(t => Sanitize($"\"{allTags[t].Name}\""))
                        //this will create tags for year and month to find post byt date to verify conversions
                        //.Union(new[] { "\"Converted\"", $"\"{post.Date.Year}/{post.Date.Month}\"" })
                        .Where(n => !"uncategorized".Equals(n))
                        .Distinct()
                        .ToList();

                    var postTags = string.Join(",", allTagEntries);
                    if (String.IsNullOrEmpty(postTags)) 
                    {
                        postTags = postCategories;
                    }

                    sb.AppendFormat(Header,
                        Sanitize(post.Title.Rendered),
                        "",
                        $"{post.Date.Year}-{post.Date.Month.ToString("00")}-{post.Date.Day.ToString("00")}T{post.Date.Hour.ToString("00")}:00:37+02:00",
                        postTags,
                        postCategories);
                    sb.Append(ConvertToMarkDown(converter, doc));

                    var newSlug = $@"old\{post.Date.Year}\{post.Date.Month.ToString("00")}\{post.Slug}";
                    string fileName = $@"{baseOutputDir}\{newSlug}.md";
                    EnsureDirectory(fileName);
                    File.WriteAllText(
                        fileName,
                        sb.ToString(),
                        Encoding.UTF8);
                    Console.WriteLine("Converted post " + post.Id);
                    redirectRules.AppendFormat(@"
<rule name=""Reroute{2}"" stopProcessing=""true"">
    <match url = ""{0}"" />
    <action type = ""Redirect"" url = ""post/general/{1}"" redirectType = ""Temporary"" />
</rule>
", post.Link.Substring("https://www.codewrecks.com/".Length).TrimEnd('/', '\\'), newSlug.Replace('\\', '/').TrimEnd('/', '\\'), index);
                }
                catch (Exception ex)
                {
                    Log.Error(ex, "Error convertig post {postId}", post.Id);
                }
                if (index % 10 == 0)
                {
                    Console.Write("Press enter to continue with the next block. Done {0}", index);
                    //Console.ReadLine();
                }
            }

            File.WriteAllText($"{baseOutputDir}\\old\\web.config.redirect", redirectRules.ToString(), Encoding.ASCII);
            Console.Write("Press enter to finish, converted {0} posts", index);
            Console.ReadLine();
        }


        private static void EnsureDirectory(string fileName)
        {
            var dinfo = new DirectoryInfo(Path.GetDirectoryName(fileName));
            if (!dinfo.Exists)
            {
                Directory.CreateDirectory(dinfo.FullName);
            }
        }

        private static string ConvertToMarkDown(ReverseMarkdown.Converter converter, HtmlDocument doc)
        {
            FixOldCodeFormatterPlugin(doc);
            var preNodes = doc.DocumentNode.SelectNodes("//pre");
            if (preNodes != null)
            {
                foreach (var node in preNodes.ToList())
                {
                    if (String.IsNullOrWhiteSpace(node.InnerText))
                    {
                        continue;
                    }

                    var text = node.OuterHtml;
                    string lang = GetLanguageByRegex(text);

                    if (lang == null)
                    {
                        lang = TryGetLanguageFromContent(node, ref text);
                    }

                    if (lang == null)
                    {
                        Log.Error("Unable to find language in pre class {text}", node.OuterHtml);
                        lang = "csharp"; //most probable lang for a snippet of code.
                        File.AppendAllText(@"x:\temp\errors.txt", node.InnerText + "\n\n\n\n");
                    }

                    if (lang == "plain") 
                    {

                    }

                    var sb = new StringBuilder();
                    sb.AppendLine("<pre>{{< highlight " + lang + " \"linenos=table,linenostart=1\" >}}");

                    text = RemoveDoubleCrLf(text);
                    sb.Append(text);
                    sb.AppendLine("{{< / highlight >}}</pre>");

                    node.ParentNode.ReplaceChild(HtmlNode.CreateNode(sb.ToString()), node);
                }
            }

            var rawConversion = converter.Convert(doc.DocumentNode.InnerHtml);
            rawConversion = rawConversion.Trim('\n', '\r');
            for (int i = 0; i < 10; i++)
            {
                rawConversion = rawConversion.Replace("\r\n\r\n\r\n", "\r\n\r\n");
            }
            rawConversion = rawConversion.Replace("** ", "**");

            var lines = rawConversion.Split("\r\n");

            var final = new StringBuilder();

            foreach (var line in lines)
            {
                var parsedLine = line;
                if (parsedLine.StartsWith("    "))
                {
                    parsedLine = parsedLine.Substring(4);
                }
                final.AppendLine(parsedLine);
            }

            var result = final.ToString();

            //ok now regexes to fix Figure xxx markdown
            result = Regex.Replace(result, @"\*\*\s*(?<inner>[^\*]+?)\s*\*\*\s*", " **${inner}** ");
            result = Regex.Replace(result, @"\*\*Figure (?<id>\d*):\s*\*\*\s*", "***Figure ${id}***: ");
            result = result.Replace(" .", ".");
            result = result.Replace("[ ", "[");
            result = result.Replace(" ]", "]");
            return result;
        }

        private static void FixOldCodeFormatterPlugin(HtmlDocument doc)
        {
            var codeSnippetWrapperCode = doc.DocumentNode.SelectNodes("//div[@id='codeSnippetWrapper']");
            if (codeSnippetWrapperCode != null)
            {
                foreach (var codeNode in codeSnippetWrapperCode)
                {
                    var sb = new StringBuilder();
                    foreach (var line in codeNode.InnerText.Split("\n"))
                    {
                        var trimmedLine = line.Trim('\n', ' ', '\r', '\t');
                        if (Regex.Match(trimmedLine, @"^\d*\:\s*").Success)
                        {
                            //we have line number
                            trimmedLine = trimmedLine.Substring(trimmedLine.IndexOf(":") + 1).TrimStart(' ');
                        }
                        if (!String.IsNullOrEmpty(trimmedLine))
                        {
                            sb.AppendLine(trimmedLine);
                        }
                    }
                    codeNode.ParentNode.ReplaceChild(HtmlNode.CreateNode(@$"<pre lang=""csharp"">{sb.ToString()}</pre>"), codeNode);
                }
            }
        }

        private static string RemoveDoubleCrLf(string text)
        {
            for (int i = 0; i < 10; i++)
            {
                text = Regex.Replace(text, @"^\s+$[\r\n]*", string.Empty, RegexOptions.Multiline);
            }

            return text;
        }

        private static readonly HashSet<string> csharpConstructs = new HashSet<string>()
        {
            " class ",
            "private void ",
            "public override",
            "()]",
            "public static ",
            "public struct ",
            "public interface",
            "public string ",
            " == "
        };

        private static readonly HashSet<string> sqlConstructs = new HashSet<string>()
        {
            "SELECT ",
            "ALTER TABLE",
            "ALTER DATABASE",
            "CREATE PROCEDURE",
            "TRUNCATE TABLE",
            "WHERE ",
            "INSERT "
        };

        private static readonly HashSet<string> javascriptConstructs = new HashSet<string>()
        {
            "$(document)",
            "(function($)",
            "$('"
        };        
        
        private static readonly HashSet<string> yamlConstruct = new HashSet<string>()
        {
            "- name:",
            "- task:",
            "parameters:"
        };

        private static readonly HashSet<string> cssConstructs = new HashSet<string>()
        {
            "position: absolute;"
        };

        private static string TryGetLanguageFromContent(HtmlNode node, ref string text)
        {
            var innerText = node.InnerText;
            if (sqlConstructs.Any(s => innerText.IndexOf(s, StringComparison.OrdinalIgnoreCase) > -1))
            {
                return "sql";
            }

            if (javascriptConstructs.Any(s => innerText.IndexOf(s, StringComparison.OrdinalIgnoreCase) > -1))
            {
                return "javascript";
            }

            if (cssConstructs.Any(s => innerText.IndexOf(s, StringComparison.OrdinalIgnoreCase) > -1))
            {
                return "css";
            }

            if (yamlConstruct.Any(s => innerText.IndexOf(s, StringComparison.OrdinalIgnoreCase) > -1))
            {
                return "yaml";
            }

            if (csharpConstructs.Any(s => innerText.IndexOf(s, StringComparison.OrdinalIgnoreCase) > -1))
            {
                return "CSharp";
            }

            //ok we need to check for other language
            if (text.Contains("&gt;") && text.Contains("&lt;"))
            {
                //ok we are in presence of an old post with preformatted html .. it is really bad so we need to deencode.
                text = text.Replace("<br>", "\n");
                return "xml";
            }

            var numberOfPotentialMethodCallMatch = Regex.Matches(text, @"\w\.\w");
            if (numberOfPotentialMethodCallMatch.Count > 2)
            {
                //potentially it is csharp
                return "csharp";
            }

            return null;
        }

        private static HashSet<string> supportedLanguageInRegex = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "csharp",
            "sql",
            "yaml",
            "jscript",
            "bash",
            "powershell",
            "xml",
            "css",
            "python",
            "java",
            "vb",
            "fsharp",
            "as3"
        };

        private static string GetLanguageByRegex(string outerHtml)
        {
            var langMatch = Regex.Match(outerHtml, "brush: (?<lang>.+?);");

            if (langMatch.Success)
            {
                var lang = langMatch.Groups["lang"].Value;
                if (supportedLanguageInRegex.Contains(lang))
                {
                    return lang;
                }
                else if (lang != "plain")
                {
                    
                }
            }

            langMatch = Regex.Match(outerHtml, @"pre\s*lang\=\""(?<lang>.*)\""");

            if (langMatch.Success)
            {
                return langMatch.Groups["lang"].Value;
            }

            return null;
        }

        private static string PreParsePostContent(Post post)
        {
            var rawContent = post.Content.Rendered;
            rawContent = rawContent.Replace("Â", " ");
            return rawContent;
        }

        private static string Sanitize(string content)
        {
            StringBuilder sb = new StringBuilder(content.Length);
            foreach (var c in content)
            {
                if (Char.IsLetterOrDigit(c) || c == ' ' || c == '-')
                {
                    sb.Append(c);
                }
            }
            return sb.ToString();
        }

        private const string Header = @"---
title: ""{0}""
description: ""{1}""
date: {2}
draft: false
tags: [{3}]
categories: [{4}]
---
";
    }
}
