using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace AgentRp.Migrations
{
    /// <inheritdoc />
    public partial class AddTransientAndStoryImageOptimization : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'IsTransient') IS NULL
                    ALTER TABLE [StoryImageAssets] ADD [IsTransient] bit NOT NULL CONSTRAINT [DF_StoryImageAssets_IsTransient] DEFAULT CAST(0 AS bit);
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizationAttemptCount') IS NULL
                    ALTER TABLE [StoryImageAssets] ADD [OptimizationAttemptCount] int NOT NULL CONSTRAINT [DF_StoryImageAssets_OptimizationAttemptCount] DEFAULT 0;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizationLastAttemptUtc') IS NULL
                    ALTER TABLE [StoryImageAssets] ADD [OptimizationLastAttemptUtc] datetime2 NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizationLastError') IS NULL
                    ALTER TABLE [StoryImageAssets] ADD [OptimizationLastError] nvarchar(max) NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizationQueuedUtc') IS NULL
                    ALTER TABLE [StoryImageAssets] ADD [OptimizationQueuedUtc] datetime2 NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizedUtc') IS NULL
                    ALTER TABLE [StoryImageAssets] ADD [OptimizedUtc] datetime2 NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'TransientExpiresUtc') IS NULL
                    ALTER TABLE [StoryImageAssets] ADD [TransientExpiresUtc] datetime2 NULL;
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'TransientSessionId') IS NULL
                    ALTER TABLE [StoryImageAssets] ADD [TransientSessionId] uniqueidentifier NULL;
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StoryImageAssets_IsTransient_TransientExpiresUtc' AND object_id = OBJECT_ID(N'[dbo].[StoryImageAssets]'))
                    CREATE INDEX [IX_StoryImageAssets_IsTransient_TransientExpiresUtc] ON [StoryImageAssets] ([IsTransient], [TransientExpiresUtc]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StoryImageAssets_OptimizedUtc_OptimizationQueuedUtc_OptimizationAttemptCount' AND object_id = OBJECT_ID(N'[dbo].[StoryImageAssets]'))
                    CREATE INDEX [IX_StoryImageAssets_OptimizedUtc_OptimizationQueuedUtc_OptimizationAttemptCount] ON [StoryImageAssets] ([OptimizedUtc], [OptimizationQueuedUtc], [OptimizationAttemptCount]);
                """);

            migrationBuilder.Sql("""
                IF NOT EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StoryImageAssets_ThreadId_TransientSessionId' AND object_id = OBJECT_ID(N'[dbo].[StoryImageAssets]'))
                    CREATE INDEX [IX_StoryImageAssets_ThreadId_TransientSessionId] ON [StoryImageAssets] ([ThreadId], [TransientSessionId]);
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StoryImageAssets_IsTransient_TransientExpiresUtc' AND object_id = OBJECT_ID(N'[dbo].[StoryImageAssets]'))
                    DROP INDEX [IX_StoryImageAssets_IsTransient_TransientExpiresUtc] ON [StoryImageAssets];
                """);

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StoryImageAssets_OptimizedUtc_OptimizationQueuedUtc_OptimizationAttemptCount' AND object_id = OBJECT_ID(N'[dbo].[StoryImageAssets]'))
                    DROP INDEX [IX_StoryImageAssets_OptimizedUtc_OptimizationQueuedUtc_OptimizationAttemptCount] ON [StoryImageAssets];
                """);

            migrationBuilder.Sql("""
                IF EXISTS (SELECT 1 FROM sys.indexes WHERE name = N'IX_StoryImageAssets_ThreadId_TransientSessionId' AND object_id = OBJECT_ID(N'[dbo].[StoryImageAssets]'))
                    DROP INDEX [IX_StoryImageAssets_ThreadId_TransientSessionId] ON [StoryImageAssets];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'IsTransient') IS NOT NULL
                    ALTER TABLE [StoryImageAssets] DROP COLUMN [IsTransient];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizationAttemptCount') IS NOT NULL
                    ALTER TABLE [StoryImageAssets] DROP COLUMN [OptimizationAttemptCount];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizationLastAttemptUtc') IS NOT NULL
                    ALTER TABLE [StoryImageAssets] DROP COLUMN [OptimizationLastAttemptUtc];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizationLastError') IS NOT NULL
                    ALTER TABLE [StoryImageAssets] DROP COLUMN [OptimizationLastError];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizationQueuedUtc') IS NOT NULL
                    ALTER TABLE [StoryImageAssets] DROP COLUMN [OptimizationQueuedUtc];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'OptimizedUtc') IS NOT NULL
                    ALTER TABLE [StoryImageAssets] DROP COLUMN [OptimizedUtc];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'TransientExpiresUtc') IS NOT NULL
                    ALTER TABLE [StoryImageAssets] DROP COLUMN [TransientExpiresUtc];
                """);

            migrationBuilder.Sql("""
                IF COL_LENGTH('dbo.StoryImageAssets', 'TransientSessionId') IS NOT NULL
                    ALTER TABLE [StoryImageAssets] DROP COLUMN [TransientSessionId];
                """);
        }
    }
}
