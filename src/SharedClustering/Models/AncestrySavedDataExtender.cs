﻿using SharedClustering.Core;
using SharedClustering.HierarchicalClustering;
using SharedClustering.Models;
using SharedClustering.Properties;
using SharedClustering.SavedData;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace SharedClustering.ViewModels
{
    internal class AncestrySavedDataExtender
    {
        private readonly AncestryMatchesRetriever _matchesRetriever;
        private readonly ISerializedMatchesWriter _serializedWriter;
        private readonly IProgressData _progressData;

        public AncestrySavedDataExtender(AncestryMatchesRetriever matchesRetriever, ISerializedMatchesWriter serializedWriter, IProgressData progressData)
        {
            _matchesRetriever = matchesRetriever;
            _serializedWriter = serializedWriter;
            _progressData = progressData;
        }

        public async Task ExtendSavedDataAsync(string guid)
        {
            var serializedMatchesReaders = new List<ISerializedMatchesReader>
            {
                new DnaGedcomAncestryMatchesReader(),
                new DnaGedcomFtdnaMatchesReader(),
                new DnaGedcomMyHeritageMatchesReader(),
                new SharedClusteringExcelMatchesReader(),
                new SharedClusteringMatchesReader(),
                new AutoClusterCsvMatchesReader(),
                new AutoClusterExcelMatchesReader(),
            };

            var matchesLoader = new MatchesLoader(serializedMatchesReaders);

            Settings.Default.Save();

            var startTime = DateTime.Now;

            var (testTakerTestId, clusterableMatches, tags) = await matchesLoader.LoadClusterableMatchesAsync(@"C:\Temp\foo.txt", 6, 6, null, _progressData);
            if (clusterableMatches == null)
            {
                return;
            }
            var matches = clusterableMatches.Where(match => _toExtend.Contains(match.Match.TestGuid)).ToList();

            // Make sure there are no more than 100 concurrent HTTP requests, to avoid overwhelming the Ancestry web site.
            var throttle = new Throttle(100);

            // Don't process more than 50 matches at once. This lets the matches finish processing completely
            // rather than opening requests for all of the matches at onces.
            var matchThrottle = new Throttle(50);

            // Now download the shared matches for each match.
            // This takes much longer than downloading the list of matches themselves..
            _progressData.Reset($"Downloading shared matches for {matches.Count} matches...", matches.Count);

            var matchIndexes = clusterableMatches.ToDictionary(match => match.Match.TestGuid, match => match.Index);

            var icwTasks = matches.Select(async match =>
            {
                await matchThrottle.WaitAsync();
                var result = await _matchesRetriever.GetMatchesInCommonAsync(guid, match.Match, false, 6, throttle, matchIndexes, false, _progressData);
                var coords = new HashSet<int>(result)
                {
                    match.Index
                };
                return new
                {
                    Index = match.Index,
                    Icw = coords,
                };
            });
            var icws = await Task.WhenAll(icwTasks);

            var icwDictionary = icws.ToDictionary(icwTask => icwTask.Index, icwTask => icwTask.Icw);

            var updatedClusterableMatches = clusterableMatches.Select(match => icwDictionary.ContainsKey(match.Index) ?
                new ClusterableMatch(match.Index, match.Match, icwDictionary[match.Index]) : match).ToList();

            // Save the downloaded data to disk.
            _progressData.Reset("Saving data...");

            var icw = updatedClusterableMatches.ToDictionary(
                match => match.Match.TestGuid,
                match => match.Coords.ToList());

            var output = new Serialized
            {
                TestTakerTestId = guid,
                Matches = updatedClusterableMatches.Select(match => match.Match).ToList(),
                MatchIndexes = matchIndexes,
                Icw = icw
            };
            var fileName = @"C:\temp\foo.txt";
            _serializedWriter.Write(fileName, output);

            var matchesWithSharedMatches = output.Icw.Where(match => match.Value.Count > 1).ToList();
            var averageSharedMatches = matchesWithSharedMatches.Sum(match => match.Value.Count - 1) / (double)matchesWithSharedMatches.Count;
            _progressData.Reset(DateTime.Now - startTime, $"Done. Downloaded {matches.Count} matches ({matchesWithSharedMatches.Count} with shared matches, averaging {averageSharedMatches:0.#} shared matches");
        }

        private HashSet<string> _toExtend = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
"34A27b13-271F-4139-8EA2-5273B0723FEF",
        };
    }
}
