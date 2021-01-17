using MongoDB.Driver;
using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using WordPressPCL;
using WordPressPCL.Models;

namespace BlogTransferApp
{
    class Program
    {
        static async Task Main(string[] args)
        {
            var client = new WordPressClient("https://www.codewrecks.com/blog/wp-json/");

            // Converter
            var converter = new Html2Markdown.Converter();

            var mongoUrl = new MongoUrl("mongodb://admin:mysuperpassword@localhost/codewrecks?authSource=admin");
            var mongoClient = new MongoClient(mongoUrl);
            var db = mongoClient.GetDatabase(mongoUrl.DatabaseName);
            var postsCollection = db.GetCollection<Post>("posts");

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
                var posts = await client.Posts.GetAll(false, true);
                db.DropCollection("posts");
                postsCollection.InsertMany(posts);
            }
            var orderedPosts = postsCollection.AsQueryable()
                .OrderBy(p => p.Date)
                .ToList();
            foreach (var post in orderedPosts)
            {
                try
                {
                    var markdown = converter.Convert(post.Content.Raw ?? post.Content.Rendered);
                    File.WriteAllText(@"c:\temp\converter\" + post.Slug + ".md", markdown);
                }
                catch (Exception)
                {
                    Console.WriteLine("Error converting {0}", post.Id);
                }
            }
        }
    }
}
