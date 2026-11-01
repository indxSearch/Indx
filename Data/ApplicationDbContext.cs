using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace IndxServer.Data
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
        public DbSet<Team> Teams => Set<Team>();
        public DbSet<TeamMember> TeamMembers => Set<TeamMember>();
        public DbSet<Invite> Invites => Set<Invite>();

        protected override void OnModelCreating(ModelBuilder builder)
        {
            base.OnModelCreating(builder);

            // Team.Name is the globally-unique, human-facing identity — enforce it as a real
            // unique index so concurrent signups racing on the same name can't both slip through.
            builder.Entity<Team>()
                .HasIndex(t => t.Name)
                .IsUnique();

            // Membership is a pure join table: one row per (team, user).
            builder.Entity<TeamMember>()
                .HasKey(m => new { m.TeamId, m.UserId });

            // These already exist in the DB (created by the AddApiKeys / AddNotifications
            // migrations) but were never declared in the model. Declare them here so the model is
            // the source of truth and the AddTeams migration stays purely additive.
            builder.Entity<ApiKey>()
                .HasIndex(k => k.Jti);
            // A personal key goes with its user. Stated, not inferred: UserId became optional
            // for team keys, and an optional key defaults to setting null on delete, which would
            // quietly turn a deleted user's personal keys into keys the team appears to own.
            builder.Entity<ApiKey>()
                .HasOne(k => k.User)
                .WithMany()
                .HasForeignKey(k => k.UserId)
                .OnDelete(DeleteBehavior.Cascade);
            // Existing keys predate scopes and keep full access.
            builder.Entity<ApiKey>()
                .Property(k => k.Level)
                .HasDefaultValue("Full");
            builder.Entity<Notification>()
                .HasIndex(n => n.UserId);
            builder.Entity<Notification>()
                .HasIndex(n => new { n.UserId, n.Type, n.SourceId });
            builder.Entity<ApplicationUser>()
                .Property(u => u.EmailNotificationsEnabled)
                .HasDefaultValue(true);
            // An invite goes with its team: a deleted team leaves no invitations to it.
            builder.Entity<Invite>()
                .HasOne(i => i.Team)
                .WithMany()
                .HasForeignKey(i => i.TeamId)
                .OnDelete(DeleteBehavior.Cascade);
            builder.Entity<Invite>()
                .HasIndex(i => i.NormalizedEmail);
            builder.Entity<Invite>()
                .HasIndex(i => i.TokenHash)
                .IsUnique();
        }
    }
#pragma warning restore 1591
}