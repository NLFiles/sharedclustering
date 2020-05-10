﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;
using AncestryDnaClustering.Models.SavedData;
using AncestryDnaClustering.ViewModels;

namespace AncestryDnaClustering.Models
{
    internal class AncestryMatchesRetriever
    {
        private AncestryLoginHelper _ancestryLoginHelper;
        public int MatchesPerPage { get; } = 200;

        public AncestryMatchesRetriever(AncestryLoginHelper ancestryLoginHelper)
        {
            _ancestryLoginHelper = ancestryLoginHelper;
        }

        public async Task<List<Match>> GetMatchesAsync(string guid, int numMatches, HashSet<int> tagIds, bool includeAdditionalInfo, Throttle throttle, ProgressData progressData)
        {
            var retryCount = 0;
            var retryMax = 5;
            while (true)
            {
                try
                {
                    var startPage = 1;
                    var numPages = (numMatches + MatchesPerPage) / MatchesPerPage - startPage + 1;

                    progressData.Reset("Downloading matches...", numPages * 2);

                    var matchesTasks = Enumerable.Range(startPage, numPages)
                        .Select(pageNumber => GetMatchesPageAsync(guid, tagIds, pageNumber, includeAdditionalInfo, throttle, progressData));
                    var matchesGroups = await Task.WhenAll(matchesTasks);
                    return matchesGroups.SelectMany(matchesGroup => matchesGroup).Take(numMatches).ToList();
                }
                catch (Exception ex)
                {
                    if (++retryCount >= retryMax)
                    {
                        throw;
                    }
                    await DelayForExceptionAsync(ex);
                }
            }
        }

        private Task DelayForExceptionAsync(Exception ex) => Task.Delay(ex is UnsupportedMediaTypeException ? 30000 : 3000);

        public async Task<MatchCounts> GetMatchCounts(string guid)
        {
            // Make sure there are no more than 10 concurrent HTTP requests, to avoid overwhelming the Ancestry web site.
            var throttle = new Throttle(10);

            var highestMatchesTask = GetMatchesPageAsync(guid, new HashSet<int>(), 1, false, throttle, ProgressData.SuppressProgress);
            var nextHighestMatchesTask = GetMatchesPageAsync(guid, new HashSet<int>(), 2, false, throttle, ProgressData.SuppressProgress);
            var thirdCousinsTask = CountThirdCousinsAsync(guid, throttle, ProgressData.SuppressProgress);
            var matchesTask = CountMatchesAsync(guid, throttle, ProgressData.SuppressProgress);
            await Task.WhenAll(thirdCousinsTask, matchesTask);

            return new MatchCounts
            {
                HighestCentimorgans = (await highestMatchesTask).FirstOrDefault()?.SharedCentimorgans ?? 4000,
                FourHundredthCentimorgans = (await nextHighestMatchesTask).LastOrDefault()?.SharedCentimorgans ?? (await highestMatchesTask).LastOrDefault()?.SharedCentimorgans ?? 50,
                ThirdCousins = await thirdCousinsTask,
                FourthCousins = (await matchesTask).fourthCousins,
                TotalMatches = (await matchesTask).totalMatches,
            };
        }

        private Task<int> CountThirdCousinsAsync(string guid, Throttle throttle, ProgressData progressData)
        {
            return CountMatches(guid, match => match.SharedCentimorgans >= 90, 1, 1, throttle, progressData);
        }

        private class MatchesCounts
        {
            public int All { get; set; }
            public int Close { get; set; }
            public int Starred { get; set; }
        }

        private async Task<(int fourthCousins, int totalMatches)> CountMatchesAsync(string guid, Throttle throttle, ProgressData progressData)
        {
            try
            {
                using (var testsResponse = await _ancestryLoginHelper.AncestryClient.GetAsync($"discoveryui-matchesservice/api/samples/{guid}/matchlist/counts"))
                {
                    testsResponse.EnsureSuccessStatusCode();
                    var matchesCounts = await testsResponse.Content.ReadAsAsync<MatchesCounts>();
                    return (matchesCounts.Close, matchesCounts.All);
                }
            }
            catch
            {
                // If any error occurs, fall through to count the matches manually. 
            }

            // Count the matches manually. 
            var fourthCousinsTask = CountMatches(guid, match => match.SharedCentimorgans >= 20, 1, 20, throttle, progressData);
            var totalMatchesTask = CountMatches(guid, _ => true, 1, 1000, throttle, progressData);
            await Task.WhenAll(fourthCousinsTask, totalMatchesTask);
            return (await fourthCousinsTask, await totalMatchesTask);
        }

        private async Task<int> CountMatches(string guid, Func<Match, bool> criteria, int minPage, int maxPage, Throttle throttle, ProgressData progressData)
        {
            IEnumerable<Match> pageMatches = new Match[0];

            // Try to find some page that is at least as high as the highest valid match.
            do
            {
                pageMatches = await GetMatchesPageAsync(guid, new HashSet<int>(), maxPage, false, throttle, progressData);
                if (pageMatches.Any(match => !criteria(match)) || !pageMatches.Any())
                {
                    break;
                }
                maxPage *= 2;
            } while (true);

            // Back down to find the the page that is exactly as high as the highest valid match
            var midPage = minPage;
            while (maxPage > minPage)
            {
                midPage = (maxPage + minPage) / 2;
                pageMatches = await GetMatchesPageAsync(guid, new HashSet<int>(), midPage, false, throttle, progressData);
                if (pageMatches.Any(match => criteria(match)))
                {
                    if (pageMatches.Any(match => !criteria(match)))
                    {
                        break;
                    }
                    minPage = midPage + 1;
                }
                else
                {
                    maxPage = midPage;
                }
            }

            return (midPage - 1) * MatchesPerPage + pageMatches.Count(match => criteria(match));
        }

        public async Task<IEnumerable<Match>> GetMatchesPageAsync(string guid, HashSet<int> tagIds, int pageNumber, bool includeAdditionalInfo, Throttle throttle, ProgressData progressData)
        {
            var nameUnavailableCount = 0;
            var nameUnavailableMax = 60;
            var retryCount = 0;
            var retryMax = 5;
            while (true)
            {
                await throttle.WaitAsync();
                var throttleReleased = false;

                try
                {
                    using (var testsResponse = await _ancestryLoginHelper.AncestryClient.GetAsync($"discoveryui-matchesservice/api/samples/{guid}/matches/list?page={pageNumber}&bookmarkdata={{\"moreMatchesAvailable\":true,\"lastMatchesServicePageIdx\":{pageNumber - 1}}}"))
                    {
                        throttle.Release();
                        throttleReleased = true;

                        testsResponse.EnsureSuccessStatusCode();
                        var matches = await testsResponse.Content.ReadAsAsync<MatchesV2>();
                        var result = matches.MatchGroups?.SelectMany(matchGroup => matchGroup.Matches)
                            .Select(match => ConvertMatch(match, tagIds))
                            .ToList() ?? new List<Match>();

                        // Sometimes Ancestry returns matches with partial data.
                        // If that happens, retry and hope to get full data the next time.
                        if (result.Any(match => match.Name == "name unavailable") && ++nameUnavailableCount < nameUnavailableMax)
                        {
                            await Task.Delay(3000);
                            continue;
                        }

                        progressData.Increment();
                        if (includeAdditionalInfo)
                        {
                            try
                            {
                                await GetAdditionalInfoAsync(guid, result, throttle);
                            }
                            catch
                            {
                                // non-fatal if unable to download trees
                            }

                            if (pageNumber == 1)
                            {
                                try
                                {
                                    await GetParentsAsync(guid, result, throttle);
                                }
                                catch
                                {
                                    // non-fatal if unable to download parents
                                }
                            }
                        }

                        progressData.Increment();
                        return result;
                    }
                }
                catch (Exception ex)
                {
                    if (++retryCount >= retryMax)
                    {
                        FileUtils.LogException(ex, true);
                        await DelayForExceptionAsync(ex);
                        return Enumerable.Empty<Match>();
                    }
                    await DelayForExceptionAsync(ex);
                }
                finally
                {
                    if (!throttleReleased)
                    {
                        throttle.Release();
                    }
                }
            }
        }

        private static Match ConvertMatch(MatchV2 match, HashSet<int> tagIds)
        {
            var tagIdsToStore = match.Tags?.Intersect(tagIds).ToList();
            return new Match
            {
                MatchTestAdminDisplayName = match.AdminDisplayName,
                MatchTestDisplayName = match.DisplayName,
                TestGuid = match.TestGuid,
                SharedCentimorgans = match.Relationship?.SharedCentimorgans ?? 0,
                SharedSegments = match.Relationship?.SharedSegments ?? 0,
                Starred = match.Starred,
                Note = match.Note,
                TagIds = tagIdsToStore?.Count > 0 ? tagIdsToStore : null,
            };
        }

        public async Task<Match> GetMatchAsync(string guid, string testGuid, HashSet<int> tagIds, Throttle throttle, ProgressData progressData)
        {
            var retryCount = 0;
            var retryMax = 5;
            while (true)
            {
                await throttle.WaitAsync();

                try
                {
                    using (var testsResponse = await _ancestryLoginHelper.AncestryClient.GetAsync($"discoveryui-matchesservice/api/samples/{guid}/matches/{testGuid}/details"))
                    {
                        if (!testsResponse.IsSuccessStatusCode)
                        {
                            return null;
                        }

                        return ConvertMatch(await testsResponse.Content.ReadAsAsync<MatchV2>(), tagIds);
                    }
                }
                catch (Exception ex)
                {
                    if (++retryCount >= retryMax)
                    {
                        FileUtils.LogException(ex, true);
                        await DelayForExceptionAsync(ex);
                        return null;
                    }
                    await DelayForExceptionAsync(ex);
                }
                finally
                {
                    progressData.Increment();
                    throttle.Release();
                }
            }
        }

        public async Task<List<Match>> GetRawMatchesInCommonAsync(string guid, string guidInCommon, int maxPage, double minSharedCentimorgans, bool throwException, Throttle throttle)
        {
            var matches = new List<Match>();
            for (var pageNumber = 1; pageNumber <= maxPage; ++pageNumber)
            {
                var originalCount = matches.Count;
                var (pageMatches, moreMatchesAvailable) = await GetMatchesInCommonPageAsync(guid, guidInCommon, pageNumber, throwException, throttle);
                matches.AddRange(pageMatches);

                // Exit if we read past the end of the list of matches (a page with no matches),
                // or if the last entry on the page is lower than the minimum.
                if (!moreMatchesAvailable
                    || originalCount == matches.Count 
                    || matches.Last().SharedCentimorgans < minSharedCentimorgans
                    || matches.Count < MatchesPerPage)
                {
                    break;
                }
            }
            return matches;
        }

        public bool WillGetMatchesInCommon(Match match, bool noSharedMatches) => !noSharedMatches || match.HasCommonAncestors;

        public async Task<List<int>> GetMatchesInCommonAsync(string guid, Match match, bool noSharedMatches, double minSharedCentimorgans, Throttle throttle, Dictionary<string, int> matchIndexes, bool throwException, ProgressData progressData)
        {
            var commonAncestorsTask = match.HasCommonAncestors ? GetCommonAncestorsAsync(guid, match.TestGuid, throttle) : Task.FromResult((List<string>)null);

            // Retrieve the matches.
            const int maxPage = 10000;
            var matches = noSharedMatches ? new List<Match>() : await GetRawMatchesInCommonAsync(guid, match.TestGuid, maxPage, minSharedCentimorgans, throwException, throttle);
            var result = matches.GroupBy(m => m.TestGuid).ToDictionary(g => g.Key, g => g.First().TestGuid);

            match.CommonAncestors = await commonAncestorsTask;

            progressData.Increment();
            return result.Keys
                .Select(matchName => matchIndexes.TryGetValue(matchName, out var matchIndex) ? matchIndex : (int?)null)
                .Where(i => i != null)
                .Select(i => i.Value)
                .Concat(new[] { matchIndexes[match.TestGuid] })
                .ToList();
        }

        private async Task<(IEnumerable<Match> matches, bool moreMatchesAvailable)> GetMatchesInCommonPageAsync(string guid, string guidInCommon, int pageNumber, bool throwException, Throttle throttle)
        {
            if (guid == guidInCommon)
            {
                return (Enumerable.Empty<Match>(), false);
            }

            var nameUnavailableCount = 0;
            var nameUnavailableMax = 60;
            var retryCount = 0;
            var retryMax = 5;
            while (true)
            {
                await throttle.WaitAsync();
                var throttleReleased = false;

                try
                {
                    using (var testsResponse = await _ancestryLoginHelper.AncestryClient.GetAsync($"discoveryui-matchesservice/api/samples/{guid}/matches/list?page={pageNumber}&relationguid={guidInCommon}&bookmarkdata={{\"moreMatchesAvailable\":true,\"lastMatchesServicePageIdx\":{pageNumber - 1}}}"))
                    {
                        throttle.Release();
                        throttleReleased = true;

                        if (testsResponse.StatusCode == System.Net.HttpStatusCode.Gone)
                        {
                            return (Enumerable.Empty<Match>(), false);
                        }
                        if (testsResponse.StatusCode == System.Net.HttpStatusCode.ServiceUnavailable)
                        {
                            await Task.Delay(120000);
                            continue;
                        }
                        testsResponse.EnsureSuccessStatusCode();

                        if (testsResponse.Content.Headers.ContentType.MediaType.Equals("text/html", StringComparison.OrdinalIgnoreCase))
                        {
                            var body = await testsResponse.Content.ReadAsStringAsync();
                            await Task.Delay(30000);
                            throw new Exception($"Unexpected response content: {body}");
                        }

                        var matches = await testsResponse.Content.ReadAsAsync<MatchesV2>();

                        var matchesInCommon = matches.MatchGroups?.SelectMany(matchGroup => matchGroup.Matches)
                            .Select(match => new Match
                            {
                                MatchTestAdminDisplayName = match.AdminDisplayName,
                                MatchTestDisplayName = match.DisplayName,
                                TestGuid = match.TestGuid,
                                SharedCentimorgans = match.Relationship?.SharedCentimorgans ?? 0,
                                SharedSegments = match.Relationship?.SharedSegments ?? 0,
                                Starred = match.Starred,
                                Note = match.Note,
                            })
                            .ToList() ?? new List<Match>();

                        if (matchesInCommon.Any(match => match.Name == "name unavailable") && ++nameUnavailableCount < nameUnavailableMax)
                        {
                            await Task.Delay(3000);
                            continue;
                        }

                        return (matchesInCommon, matches.BookmarkData.MoreMatchesAvailable); 
                    }
                }
                catch (Exception ex)
                {
                    if (++retryCount >= retryMax)
                    {
                        FileUtils.LogException(ex, true);
                        await DelayForExceptionAsync(ex);
                        if (throwException)
                        {
                            throw;
                        }
                        return (Enumerable.Empty<Match>(), false);
                    }
                    await DelayForExceptionAsync(ex);
                }
                finally
                {
                    if (!throttleReleased)
                    {
                        throttle.Release();
                    }
                }
            }
        }

        private class Matches
        {
            public List<MatchGroup> MatchGroups { get; set; }
            public int PageCount { get; set; }
        }

        private class MatchGroup
        {
            public List<Match> Matches { get; set; }
        }

        private class MatchesV2
        {
            public List<MatchGroupV2> MatchGroups { get; set; }
            public BookmarkDataV2 BookmarkData { get; set; }
        }

        private class BookmarkDataV2
        {
            public int LastMatchesServicePageIdx { get; set; }
            public bool MoreMatchesAvailable { get; set; }
        }

        private class MatchGroupV2
        {
            public List<MatchV2> Matches { get; set; }
        }

        private class MatchV2
        {
            public string AdminDisplayName { get; set; }
            public string DisplayName { get; set; }
            public string TestGuid { get; set; }
            public Relationship Relationship { get; set; }
            public bool Starred { get; set; }
            public string Note { get; set; }
            public List<int> Tags { get; set; }
        }

        private class Relationship
        {
            public double SharedCentimorgans { get; set; }
            public int SharedSegments { get; set; }
        }

        private async Task GetAdditionalInfoAsync(string guid, IEnumerable<Match> matches, Throttle throttle)
        {
            while (true)
            {
                await throttle.WaitAsync();
                var throttleReleased = false;

                try
                {
                    var matchesDictionary = matches.ToDictionary(match => match.TestGuid);
                    var url = $"/discoveryui-matchesservice/api/samples/{guid}/matchesv2/additionalInfo?ids=[{"%22" + string.Join("%22,%22", matchesDictionary.Keys) + "%22"}]&ancestors=true&tree=true";
                    using (var testsResponse = await _ancestryLoginHelper.AncestryClient.GetAsync(url))
                    {
                        throttle.Release();
                        throttleReleased = true;

                        testsResponse.EnsureSuccessStatusCode();
                        var undeterminedCount = 0;
                        var additionalInfos = await testsResponse.Content.ReadAsAsync<List<AdditionalInfo>>();
                        foreach (var additionalInfo in additionalInfos)
                        {
                            if (matchesDictionary.TryGetValue(additionalInfo.TestGuid, out var match))
                            {
                                match.TreeSize = additionalInfo.TreeSize ?? 0;
                                match.TreeType = 
                                      additionalInfo.PrivateTree == true ? TreeType.Private // might also be unlinked
                                    : additionalInfo.UnlinkedTree == true ? TreeType.Unlinked
                                    : additionalInfo.PublicTree == true && match.TreeSize > 0 ? TreeType.Public
                                    : additionalInfo.NoTrees == true ? TreeType.None
                                    : TreeType.Undetermined;
                                if (match.TreeType == TreeType.Undetermined)
                                {
                                    ++undeterminedCount;
                                }
                                match.HasCommonAncestors = additionalInfo.CommonAncestors ?? false;
                            }
                        }

                        return;
                    }
                }
                catch (Exception ex)
                {
                }
                finally
                {
                    if (!throttleReleased)
                    {
                        throttle.Release();
                    }
                }
            }
        }

        private class ParentsInfo
        {
            public string MotherSampleId { get; set; }
            public string FatherSampleId { get; set; }
        }

        private async Task GetParentsAsync(string guid, IEnumerable<Match> matches, Throttle throttle)
        {
            await throttle.WaitAsync();

            try
            {
                var url = $"/discoveryui-matchesservice/api/samples/{guid}/matchesv2/parents";
                using (var testsResponse = await _ancestryLoginHelper.AncestryClient.GetAsync(url))
                {
                    testsResponse.EnsureSuccessStatusCode();
                    var parentsInfo = await testsResponse.Content.ReadAsAsync<ParentsInfo>();
                    if (!string.IsNullOrEmpty(parentsInfo.FatherSampleId) || !string.IsNullOrEmpty(parentsInfo.MotherSampleId))
                    {
                        foreach (var match in matches)
                        {
                            if (match.TestGuid == parentsInfo.FatherSampleId)
                            {
                                match.IsFather = true;
                            }
                            if (match.TestGuid == parentsInfo.MotherSampleId)
                            {
                                match.IsMother = true;
                            }
                        }
                    }
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        public async Task<List<Tag>> GetTagsAsync(string guid, Throttle throttle)
        {
            await throttle.WaitAsync();

            try
            {
                var url = $"/discoveryui-matchesservice/api/samples/{guid}/tags";
                using (var testsResponse = await _ancestryLoginHelper.AncestryClient.GetAsync(url))
                {
                    testsResponse.EnsureSuccessStatusCode();
                    return await testsResponse.Content.ReadAsAsync<List<Tag>>();
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        private class Ancestors
        {
            public List<AncestorCouple> AncestorCouples { get; set; }
        }

        private class AncestorCouple
        {
            public Ancestor Father { get; set; }
            public Ancestor Mother { get; set; }
        }

        private class Ancestor
        {
            public PersonData PersonData { get; set; }
        }

        private class PersonData
        {
            public string DisplayName { get; set; }
            public bool Potential { get; set; }
        }

        private async Task<List<string>> GetCommonAncestorsAsync(string guid, string testGuid, Throttle throttle)
        {
            while (true)
            {
                await throttle.WaitAsync();
                var throttleReleased = false;

                try
                {
                    var url = $"/discoveryui-matchesservice/api/compare/{guid}/with/{testGuid}/commonancestors/";
                    using (var testsResponse = await _ancestryLoginHelper.AncestryClient.GetAsync(url))
                    {
                        throttle.Release();
                        throttleReleased = true;

                        testsResponse.EnsureSuccessStatusCode();
                        var ancestorCouples = await testsResponse.Content.ReadAsAsync<Ancestors>();
                        var result = ancestorCouples.AncestorCouples.SelectMany(couple => new[] { couple.Father, couple.Mother })
                            .Select(ancestor => ancestor?.PersonData?.DisplayName)
                            .Where(name => !string.IsNullOrEmpty(name))
                            .ToList();
                        return result.Count > 0 ? result : null;
                    }
                }
                catch (Exception ex)
                {
                }
                finally
                {
                    if (!throttleReleased)
                    {
                        throttle.Release();
                    }
                }
            }
        }

        public async Task UpdateNotesAsync(string guid, string testGuid, string note, Throttle throttle)
        {
            await throttle.WaitAsync();

            try
            {
                var url = $"/discoveryui-matchesservice/api/samples/{guid}/matches/{testGuid}";
                using (var testsResponse = await _ancestryLoginHelper.AncestryClient.PutAsJsonAsync(url, new { note }))
                {
                    testsResponse.EnsureSuccessStatusCode();
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        public async Task UpdateStarredAsync(string guid, string testGuid, bool starred, Throttle throttle)
        {
            await throttle.WaitAsync();

            try
            {
                var url = $"/discoveryui-matchesservice/api/samples/{guid}/matches/{testGuid}";
                using (var testsResponse = await _ancestryLoginHelper.AncestryClient.PutAsJsonAsync(url, new { starred }))
                {
                    testsResponse.EnsureSuccessStatusCode();
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        public async Task AddTagAsync(string guid, string testGuid, int tagId, Throttle throttle)
        {
            await throttle.WaitAsync();

            try
            {
                var url = $"/discoveryui-matchesservice/api/samples/{guid}/matches/{testGuid}/tags/{tagId}";
                var request = new
                {
                    headers = new
                    {
                        normalizedNames = new { },
                        lazyUpdate = (bool?)null,
                        lazyInit = (bool?)null,
                        headers = new { },
                    }
                };
                using (var testsResponse = await _ancestryLoginHelper.AncestryClient.PutAsJsonAsync(url, request))
                {
                    testsResponse.EnsureSuccessStatusCode();
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        public async Task DeleteTagAsync(string guid, string testGuid, int tagId, Throttle throttle)
        {
            await throttle.WaitAsync();

            try
            {
                var url = $"/discoveryui-matchesservice/api/samples/{guid}/matches/{testGuid}/tags/{tagId}";
                using (var testsResponse = await _ancestryLoginHelper.AncestryClient.DeleteAsync(url))
                {
                    testsResponse.EnsureSuccessStatusCode();
                }
            }
            finally
            {
                throttle.Release();
            }
        }

        private class TreeInfo
        {
            public bool HasHint { get; set; }
            public bool HasUnlinkedTree { get; set; }
            public int PersonCount { get; set; }
            public bool PrivateTree { get; set; }
        }

        private class AdditionalInfo
        {
            public bool? NoTrees { get; set; }
            public bool? PrivateTree { get; set; }
            public bool? PublicTree { get; set; }
            public string TestGuid { get; set; }
            public string TreeId { get; set; }
            public int? TreeSize { get; set; }
            public bool? TreeUnavailable { get; set; }
            public bool? UnlinkedTree { get; set; }
            public bool? CommonAncestors { get; set; }
        }

        private class LinkedTreeDetails
        {
            public bool PrivateTree { get; set; }
            public int PersonCount { get; set; }
        }

        private class TreeDetails
        {
            public bool MatchTestHasTree { get; set; }
            public int MatchTreeNodeCount { get; set; }
            public List<PublicTreeInformation> PublicTreeInformationList { get; set; }
        }

        private class PublicTreeInformation
        {
            public bool IsPublic { get; set; }
        }
    }
}
