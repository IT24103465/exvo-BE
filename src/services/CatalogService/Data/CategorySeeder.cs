using Exvo.CatalogService.Models;
using Microsoft.EntityFrameworkCore;

namespace Exvo.CatalogService.Data;

public static class CategorySeeder
{
    public static async Task SeedAsync(CatalogDbContext db)
    {
        try
        {
            // Do not call MigrateAsync if root connection without DB is blocked on Azure MySQL
            // await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            Console.WriteLine($"Migration warning: {ex.Message}");
        }

        try
        {
            var defaults = new (string Name, string Description)[]
            {
                ("Music & Concerts", "Live music events and festivals"),
                ("Concert", "Concerts and live performances"),
                ("Festival", "Music and cultural festivals"),
                ("Live Session", "Intimate live sessions"),
                ("DJ Night", "EDM and DJ night events"),
                ("Acoustic", "Unplugged acoustic sets"),
                ("Stand-Up", "Comedy and stand-up shows"),
                ("EDM Arena", "Electronic dance music festivals"),
            };

            var existingNames = new HashSet<string>(
                await db.Categories.Select(c => c.Name).ToListAsync(),
                StringComparer.OrdinalIgnoreCase);

            bool addedAny = false;
            foreach (var (name, description) in defaults)
            {
                if (existingNames.Add(name))
                {
                    db.Categories.Add(new Category { Name = name, Description = description });
                    addedAny = true;
                }
            }

            if (addedAny)
            {
                await db.SaveChangesAsync();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"CategorySeeder warning: {ex.Message}");
        }
    }
}
