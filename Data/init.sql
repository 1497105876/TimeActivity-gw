-- ============================================
-- TimeActivity 数据库建表脚本
-- 数据库：TimeActivityDB
-- ============================================

CREATE DATABASE TimeActivityDB;
GO

USE TimeActivityDB;
GO

-- 1. 分类表
CREATE TABLE Categories (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Name NVARCHAR(64) NOT NULL,
    Color NVARCHAR(16) NOT NULL DEFAULT '#808080',
    Icon NVARCHAR(64) NOT NULL DEFAULT '',
    SortOrder INT NOT NULL DEFAULT 0
);

-- 2. 活动记录表
CREATE TABLE Activities (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    ProcessName NVARCHAR(256) NOT NULL,
    WindowTitle NVARCHAR(512) NOT NULL DEFAULT '',
    Category NVARCHAR(64) NOT NULL DEFAULT '未分类',
    StartTime DATETIME2 NOT NULL,
    EndTime DATETIME2 NOT NULL,
    Duration INT NOT NULL DEFAULT 0,  -- 秒
    IsIdle BIT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
);

-- 3. 分类规则表
CREATE TABLE Rules (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    ProcessName NVARCHAR(256) NOT NULL,
    TitleKeyword NVARCHAR(256) NULL,
    CategoryId INT NOT NULL,
    IsCustom BIT NOT NULL DEFAULT 0,
    FOREIGN KEY (CategoryId) REFERENCES Categories(Id)
);

-- 4. 截图记录表
CREATE TABLE Screenshots (
    Id BIGINT IDENTITY(1,1) PRIMARY KEY,
    FilePath NVARCHAR(512) NOT NULL,
    CapturedAt DATETIME2 NOT NULL,
    FileSize INT NOT NULL DEFAULT 0,
    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
);

-- 5. 每日汇总表
CREATE TABLE DailySummaries (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Date DATE NOT NULL UNIQUE,
    TotalActiveTime INT NOT NULL DEFAULT 0,  -- 秒
    CategoryBreakdown NVARCHAR(MAX) NULL,    -- JSON
    TopApps NVARCHAR(MAX) NULL,              -- JSON
    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
);

-- 6. AI 总结记录表
CREATE TABLE AISummaries (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    Date DATE NOT NULL,
    SummaryText NVARCHAR(MAX) NOT NULL,
    SummaryType NVARCHAR(32) NOT NULL DEFAULT 'daily',  -- daily/weekly/habit
    CreatedAt DATETIME2 NOT NULL DEFAULT GETDATE()
);

-- 7. 设置表
CREATE TABLE Settings (
    Id INT IDENTITY(1,1) PRIMARY KEY,
    [Key] NVARCHAR(128) NOT NULL UNIQUE,
    [Value] NVARCHAR(512) NULL
);

-- ============================================
-- 初始数据
-- ============================================

-- 预置分类
INSERT INTO Categories (Name, Color, Icon, SortOrder) VALUES
(N'开发', '#4A90D9', 'code', 1),
(N'社交', '#E67E22', 'chat', 2),
(N'娱乐', '#E74C3C', 'gamepad', 3),
(N'学习', '#2ECC71', 'book', 4),
(N'系统', '#95A5A6', 'desktop', 5),
(N'网页', '#9B59B6', 'globe', 6),
(N'空闲', '#BDC3C7', 'coffee', 7),
(N'未分类', '#7F8C8D', 'question', 8);

-- 预置设置项
INSERT INTO Settings ([Key], [Value]) VALUES
('PollIntervalSeconds', '3'),
('IdleThresholdSeconds', '300'),
('AutoStartTracking', 'true'),
('TrackWindowTitle', 'true'),
('EnableScreenshot', 'false'),
('ScreenshotIntervalMinutes', '5'),
('ScreenshotPath', ''),
('ScreenshotQuality', 'medium'),
('ColorScheme', 'default'),
('Use24Hour', 'true'),
('Theme', 'light'),
('DataRetentionDays', '90'),
('EnableAI', 'true'),
('AIApiUrl', 'https://api.minimax.chat/v1/text/chatcompletion_v2'),
('AIApiKey', ''),
('AutoDailySummary', 'true'),
('AutoWeeklySummary', 'true'),
('AutoMonthlySummary', 'true'),
('AutoStartWithWindows', 'true'),
('MinimizeToTray', 'true'),
('HotkeyToggleTracking', 'Ctrl+Shift+T');

-- ============================================
-- 索引
-- ============================================

CREATE INDEX IX_Activities_StartTime ON Activities(StartTime);
CREATE INDEX IX_Activities_Category ON Activities(Category);
CREATE INDEX IX_Activities_ProcessName ON Activities(ProcessName);
CREATE INDEX IX_Screenshots_CapturedAt ON Screenshots(CapturedAt);
CREATE INDEX IX_DailySummaries_Date ON DailySummaries(Date);
