using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Windows.Media;
using JRETS.Go.Core.Configuration;
using NAudio.Wave;

namespace JRETS.Go.App.Services;

public sealed class AnnouncementAudioService
{
    private const double AnnouncementTargetRms = 0.12;
    private const double AnnouncementMinGain = 0.8;
    private const double AnnouncementMaxGain = 6.0;
    private const double AnnouncementLimiterThreshold = 0.98;
    private const double AnnouncementSoftClipDrive = 1.8;

    public bool HasStationAnnouncement(LineConfiguration lineConfiguration, string trainId, int stationId, int paIndex)
    {
        var station = lineConfiguration.Stations.FirstOrDefault(x => x.Id == stationId);
        if (station is null)
        {
            return false;
        }

        var paList = ResolvePaListForService(station, trainId);
        return paList is not null
            && paList.Count > paIndex
            && !string.IsNullOrWhiteSpace(paList[paIndex].FileName);
    }

    public double ResolveAnnouncementTriggerDistanceMeters(
        LineConfiguration lineConfiguration,
        string trainId,
        int stationId,
        int paIndex,
        double defaultDistanceMeters)
    {
        var station = lineConfiguration.Stations.FirstOrDefault(x => x.Id == stationId);
        if (station is null)
        {
            return defaultDistanceMeters;
        }

        var paList = ResolvePaListForService(station, trainId);
        if (paList is null || paList.Count <= paIndex)
        {
            return defaultDistanceMeters;
        }

        return paList[paIndex].TriggerDistanceMeters ?? defaultDistanceMeters;
    }

    public bool TryPlayStationAnnouncement(
        LineConfiguration lineConfiguration,
        string lineConfigPath,
        string trainId,
        int stationId,
        int paIndex,
        MediaPlayer player,
        string announcementTempDirectory,
        ConcurrentDictionary<string, string> normalizedPathCache,
        out string? error)
    {
        error = null;

        var station = lineConfiguration.Stations.FirstOrDefault(x => x.Id == stationId);
        if (station is null)
        {
            return false;
        }

        var paList = ResolvePaListForService(station, trainId);
        if (paList is null || paList.Count <= paIndex)
        {
            return false;
        }

        var fileName = paList[paIndex].FileName;
        if (string.IsNullOrWhiteSpace(fileName))
        {
            return false;
        }

        var lineId = string.IsNullOrWhiteSpace(lineConfiguration.LineInfo.Id)
            ? Path.GetFileNameWithoutExtension(lineConfigPath)
            : lineConfiguration.LineInfo.Id;

        var audioPath = Path.Combine(AppContext.BaseDirectory, "audio", lineId, trainId, fileName);
        if (!File.Exists(audioPath))
        {
            error = $"Announcement file not found: {audioPath}";
            return false;
        }

        try
        {
            var playbackPath = PrepareNormalizedAudio(audioPath, announcementTempDirectory, normalizedPathCache);
            player.Stop();
            player.Open(new Uri(playbackPath, UriKind.Absolute));
            player.Volume = 1.0;
            player.Position = TimeSpan.Zero;
            player.Play();
            return true;
        }
        catch (Exception ex)
        {
            error = $"Announcement playback failed: {ex.Message}";
            return false;
        }
    }

    public string PrepareNormalizedAudio(
        string sourcePath,
        string announcementTempDirectory,
        ConcurrentDictionary<string, string> normalizedPathCache)
    {
        Directory.CreateDirectory(announcementTempDirectory);

        var versionKey = $"{sourcePath}|{File.GetLastWriteTimeUtc(sourcePath).Ticks}";
        if (normalizedPathCache.TryGetValue(versionKey, out var cachedPath) && File.Exists(cachedPath))
        {
            return cachedPath;
        }

        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(versionKey)));
        var outputPath = Path.Combine(announcementTempDirectory, $"{hash}.wav");

        if (!File.Exists(outputPath))
        {
            NormalizeToWav(sourcePath, outputPath);
        }

        normalizedPathCache[versionKey] = outputPath;
        return outputPath;
    }

    public void TryCleanupTempDirectory(string announcementTempDirectory, TimeSpan maxAge)
    {
        try
        {
            if (!Directory.Exists(announcementTempDirectory))
            {
                return;
            }

            var cutoff = DateTime.UtcNow.Subtract(maxAge);
            foreach (var file in Directory.EnumerateFiles(announcementTempDirectory, "*.wav"))
            {
                try
                {
                    var lastWrite = File.GetLastWriteTimeUtc(file);
                    if (lastWrite < cutoff)
                    {
                        File.Delete(file);
                    }
                }
                catch
                {
                    // Ignore cleanup failures; runtime playback should not be blocked.
                }
            }
        }
        catch
        {
            // Ignore cleanup failures; runtime playback should not be blocked.
        }
    }

    private static IReadOnlyList<PaAnnouncementEntry>? ResolvePaListForService(StationInfo station, string trainId)
    {
        if (station.Pa.TryGetValue(trainId, out var directMatch))
        {
            return directMatch;
        }

        var caseInsensitiveMatch = station.Pa
            .FirstOrDefault(x => string.Equals(x.Key, trainId, StringComparison.OrdinalIgnoreCase));
        return caseInsensitiveMatch.Value;
    }

    private static void NormalizeToWav(string sourcePath, string outputPath)
    {
        using var reader = new AudioFileReader(sourcePath);
        var samples = new List<float>(reader.WaveFormat.SampleRate * Math.Max(1, reader.WaveFormat.Channels) * 6);
        var buffer = new float[reader.WaveFormat.SampleRate * Math.Max(1, reader.WaveFormat.Channels)];

        double sumSquares = 0;
        var sampleCount = 0;
        var peak = 0.0;

        while (true)
        {
            var read = reader.Read(buffer, 0, buffer.Length);
            if (read <= 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var sample = buffer[i];
                samples.Add(sample);
                sumSquares += sample * sample;
                sampleCount++;
                var abs = Math.Abs(sample);
                if (abs > peak)
                {
                    peak = abs;
                }
            }
        }

        if (sampleCount == 0)
        {
            throw new InvalidOperationException("Audio file has no readable samples.");
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        var gain = AnnouncementTargetRms / Math.Max(rms, 0.0001);
        gain = Math.Clamp(gain, AnnouncementMinGain, AnnouncementMaxGain);

        if (peak > 0.0001)
        {
            gain = Math.Min(gain, AnnouncementLimiterThreshold / peak);
        }

        var softClipNormalizer = Math.Tanh(AnnouncementSoftClipDrive);
        var processedPeak = 0.0;

        for (var i = 0; i < samples.Count; i++)
        {
            var boosted = samples[i] * gain;
            var clipped = Math.Tanh(boosted * AnnouncementSoftClipDrive) / softClipNormalizer;
            var clamped = Math.Clamp(clipped, -1.0, 1.0);
            var abs = Math.Abs(clamped);
            if (abs > processedPeak)
            {
                processedPeak = abs;
            }

            samples[i] = (float)clamped;
        }

        var finalScale = processedPeak <= 0.0001
            ? 1.0
            : Math.Min(1.0, AnnouncementLimiterThreshold / processedPeak);

        var outFormat = new WaveFormat(reader.WaveFormat.SampleRate, 16, reader.WaveFormat.Channels);
        using var writer = new WaveFileWriter(outputPath, outFormat);
        var byteBuffer = new byte[samples.Count * sizeof(short)];

        for (var i = 0; i < samples.Count; i++)
        {
            var scaled = samples[i] * (float)finalScale;
            var sample16 = (short)Math.Round(Math.Clamp(scaled, -1f, 1f) * short.MaxValue);
            byteBuffer[i * 2] = (byte)(sample16 & 0xFF);
            byteBuffer[i * 2 + 1] = (byte)((sample16 >> 8) & 0xFF);
        }

        writer.Write(byteBuffer, 0, byteBuffer.Length);
    }
}
