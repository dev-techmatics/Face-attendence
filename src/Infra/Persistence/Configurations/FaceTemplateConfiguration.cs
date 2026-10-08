using FaceAttendance.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace FaceAttendance.Infrastructure.Persistence.Configurations;

public sealed class FaceTemplateConfiguration : IEntityTypeConfiguration<FaceTemplate>
{
    /// <summary>
    /// A 512-d float32 embedding is 2048 bytes. The cap leaves room for larger models and for
    /// provider-managed references without reaching for varbinary(max), which would push the
    /// payload off-row and make the candidate scan noticeably slower.
    /// </summary>
    private const int MaxTemplateBytes = 8192;

    public void Configure(EntityTypeBuilder<FaceTemplate> builder)
    {
        builder.ToTable("FaceTemplates");

        builder.HasKey(t => t.Id);

        builder.Property(t => t.Pose).HasConversion<int>();

        builder.Property(t => t.ProviderId)
            .HasMaxLength(128)
            .IsRequired();

        builder.Property(t => t.TemplateData)
            .HasMaxLength(MaxTemplateBytes)
            .IsRequired();

        builder.Property(t => t.ImageObjectKey)
            .HasMaxLength(512)
            .IsRequired();

        builder.Property(t => t.ImageContentType)
            .HasMaxLength(64)
            .IsRequired();

        builder.Property(t => t.ImageSha256)
            .HasMaxLength(64)
            .IsRequired();

        // The query behind every verification: active templates for a person.
        builder.HasIndex(t => new { t.PersonId, t.IsActive, t.Pose })
            .HasDatabaseName("IX_FaceTemplates_Person_Active_Pose");

        // Swapping the recognition model has to find its orphans quickly.
        builder.HasIndex(t => new { t.ProviderId, t.IsActive })
            .HasDatabaseName("IX_FaceTemplates_Provider_Active");

        // Backs the "same frame re-sent for another pose" check.
        builder.HasIndex(t => new { t.PersonId, t.ImageSha256 })
            .HasDatabaseName("IX_FaceTemplates_Person_ImageHash");
    }
}
