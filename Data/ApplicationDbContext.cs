using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IndxCloudApi.Data
{
#pragma warning disable 1591
    public class ApplicationDbContext : IdentityDbContext<ApplicationUser>
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
        public DbSet<Notification> Notifications => Set<Notification>();
    }
#pragma warning restore 1591
}