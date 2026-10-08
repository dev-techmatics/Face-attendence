using Amazon.Rekognition;
using Amazon.S3;
using FaceAttendance.Core.Abstractions;
using FaceAttendance.Core.Configuration;
using FaceAttendance.Core.Services;
using FaceAttendance.Infrastructure.Persistence;
using FaceAttendance.Infrastructure.Providers.Aws;
using FaceAttendance.Infrastructure.Providers.Development;
using FaceAttendance.Infrastructure.Providers.Onnx;
using FaceAttendance.Infrastructure.Repositories;
using FaceAttendance.Infrastructure.Storage;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace FaceAttendance.Infrastructure;

public static class DependencyInjection
{
    public static IServiceCollection AddFaceAttendance(
        this IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        services.AddOptions<FaceRecognitionOptions>()
            .Bind(configuration.GetSection(FaceRecognitionOptions.SectionName))
            // Validate at startup, not on first use: a bad threshold should stop a deploy, not
            // surface as every employee being rejected at the morning rush.
            .ValidateOnStart();

        services.AddSingleton<IPostConfigureOptions<FaceRecognitionOptions>, FaceRecognitionOptionsSetup>();
        services.AddSingleton<IValidateOptions<FaceRecognitionOptions>, FaceRecognitionOptionsValidator>();
        services.Configure<StorageOptions>(configuration.GetSection(StorageOptions.SectionName));

        AddPersistence(services, configuration);
        AddImageStore(services, configuration);
        AddRecognitionProvider(services, configuration, isDevelopment);

        services.AddSingleton<IClock, SystemClock>();
        services.AddHostedService<ProbeImageRetentionService>();
        services.AddScoped<FaceCaptureValidator>();
        services.AddScoped<EnrollmentService>();
        services.AddScoped<AttendanceService>();

        return services;
    }

    private static void AddPersistence(IServiceCollection services, IConfiguration configuration)
    {
        string connectionString = configuration.GetConnectionString("Default")
            ?? throw new InvalidOperationException(
                "ConnectionStrings:Default is required. For local SQL Server Express this looks " +
                @"like: Server=.\SQLEXPRESS;Database=FaceAttendance;Trusted_Connection=True;TrustServerCertificate=True");

        services.AddDbContext<AppDbContext>(options =>
            options.UseSqlServer(connectionString, sql =>
            {
                sql.EnableRetryOnFailure(
                    maxRetryCount: 3,
                    maxRetryDelay: TimeSpan.FromSeconds(2),
                    errorNumbersToAdd: null);
                sql.CommandTimeout(30);
            }));

        services.AddScoped<IPersonRepository, PersonRepository>();
        services.AddScoped<IFaceTemplateRepository, FaceTemplateRepository>();
        services.AddScoped<IAttendanceRepository, AttendanceRepository>();
        services.AddScoped<IUnitOfWork>(sp => sp.GetRequiredService<AppDbContext>());
    }

    private static void AddImageStore(IServiceCollection services, IConfiguration configuration)
    {
        string provider = configuration[$"{StorageOptions.SectionName}:Provider"] ?? "local";

        if (provider.Equals("s3", StringComparison.OrdinalIgnoreCase))
        {
            services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(
                Amazon.RegionEndpoint.GetBySystemName(
                    configuration[$"{StorageOptions.SectionName}:S3:Region"] ?? "ap-south-1")));

            services.AddSingleton<IFaceImageStore, S3FaceImageStore>();
            return;
        }

        services.AddSingleton<IFaceImageStore, LocalFileFaceImageStore>();
    }

    private static void AddRecognitionProvider(
        IServiceCollection services,
        IConfiguration configuration,
        bool isDevelopment)
    {
        string provider = configuration[$"{FaceRecognitionOptions.SectionName}:Provider"] ?? "onnx";

        switch (provider.ToLowerInvariant())
        {
            case "development":
                // A stub that matches on file name must never be reachable in a real
                // environment: it would accept anybody and stamp it as attendance.
                if (!isDevelopment)
                {
                    throw new InvalidOperationException(
                        "FaceRecognition:Provider is 'development', which performs no real face " +
                        "recognition, but the host environment is not Development. Set the provider " +
                        "to 'onnx' or 'aws'.");
                }

                services.AddSingleton<IFaceRecognitionProvider, DevelopmentFaceRecognitionProvider>();
                break;

            case "aws":
                services.AddSingleton<IAmazonRekognition>(_ => new AmazonRekognitionClient(
                    Amazon.RegionEndpoint.GetBySystemName(
                        configuration[$"{FaceRecognitionOptions.SectionName}:Aws:Region"] ?? "ap-south-1")));

                services.AddSingleton<IFaceRecognitionProvider, RekognitionFaceRecognitionProvider>();
                break;

            case "onnx":
                // Singleton on purpose: an InferenceSession is expensive to build and safe to
                // share across threads. Per-request sessions would exhaust memory immediately.
                services.AddSingleton<IFaceRecognitionProvider, OnnxFaceRecognitionProvider>();
                break;

            default:
                throw new InvalidOperationException(
                    $"Unknown FaceRecognition:Provider '{provider}'. Valid values: onnx, aws, development.");
        }
    }
}
