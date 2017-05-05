﻿// Copyright (c) Microsoft.  All Rights Reserved.  Licensed under the Apache License, Version 2.0.  See License.txt in the project root for license information.

using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Options;
using Roslyn.Utilities;

namespace Microsoft.CodeAnalysis.Remote
{
    /// <summary>
    /// Provide solution from given checksum
    /// 
    /// TODO: change this to workspace service
    /// </summary>
    internal class SolutionService
    {
        private static readonly SemaphoreSlim s_gate = new SemaphoreSlim(initialCount: 1);
        private static readonly RemoteWorkspace s_primaryWorkspace = new RemoteWorkspace();

        private readonly AssetService _assetService;

        // this simple cache hold onto the last and primary solution created
        private volatile static Tuple<Checksum, Solution> s_primarySolution;
        private volatile static Tuple<Checksum, Solution> s_lastSolution;

        public SolutionService(AssetService assetService)
        {
            _assetService = assetService;
        }

        public async Task<Solution> GetSolutionAsync(Checksum solutionChecksum, CancellationToken cancellationToken)
        {
            var currentSolution = GetAvailableSolution(solutionChecksum);
            if (currentSolution != null)
            { 
                Debug.WriteLine("**** Cache Hit outside of lock " + (currentSolution.Workspace is RemoteWorkspace).ToString());
                return currentSolution;
            }

            // make sure there is always only one that creates a new solution
            using (await s_gate.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
            {
                currentSolution = GetAvailableSolution(solutionChecksum);
                if (currentSolution != null)
                {
                    Debug.WriteLine("**** Cache Hit inside of lock " + (currentSolution.Workspace is RemoteWorkspace).ToString());
                    return currentSolution;
                }

                var solution = await CreateSolution_NoLockAsync(solutionChecksum, s_primaryWorkspace.CurrentSolution, cancellationToken).ConfigureAwait(false);
                s_lastSolution = Tuple.Create(solutionChecksum, solution);

                return solution;
            }
        }

        public async Task UpdatePrimaryWorkspaceAsync(Checksum solutionChecksum, CancellationToken cancellationToken)
        {
            var currentSolution = s_primaryWorkspace.CurrentSolution;

            var primarySolutionChecksum = await currentSolution.State.GetChecksumAsync(cancellationToken).ConfigureAwait(false);
            if (primarySolutionChecksum == solutionChecksum)
            {
                // nothing changed
                Debug.WriteLine("**** Primary workspace up to date");
                return;
            }

            using (await s_gate.DisposableWaitAsync(cancellationToken).ConfigureAwait(false))
            {
                var solution = await UpdatePrimaryWorkspace_NoLockAsync(solutionChecksum, currentSolution, cancellationToken).ConfigureAwait(false);
                s_primarySolution = Tuple.Create(solutionChecksum, solution);
            }
        }

        private async Task<Solution> CreateSolution_NoLockAsync(Checksum solutionChecksum, Solution baseSolution, CancellationToken cancellationToken)
        {
            var updater = new SolutionCreator(_assetService, baseSolution, cancellationToken);

            if (await updater.IsIncrementalUpdateAsync(solutionChecksum).ConfigureAwait(false))
            {
                // solution has updated
                Debug.WriteLine("**** Incremental solution update!");
                return await updater.CreateSolutionAsync(solutionChecksum).ConfigureAwait(false);
            }

            // new solution. bulk sync all asset for the solution
            await _assetService.SynchronizeSolutionAssetsAsync(solutionChecksum, cancellationToken).ConfigureAwait(false);

            Debug.WriteLine("**** Not same as primary workspace! new temporary workspace created!");
            var workspace = new TemporaryWorkspace(await updater.CreateSolutionInfoAsync(solutionChecksum).ConfigureAwait(false));
            return workspace.CurrentSolution;
        }

        private async Task<Solution> UpdatePrimaryWorkspace_NoLockAsync(Checksum solutionChecksum, Solution baseSolution, CancellationToken cancellationToken)
        {
            var updater = new SolutionCreator(_assetService, baseSolution, cancellationToken);

            if (await updater.IsIncrementalUpdateAsync(solutionChecksum).ConfigureAwait(false))
            {
                // solution has updated
                Debug.WriteLine("**** Primary worksace incremental update!");
                s_primaryWorkspace.UpdateSolution(await updater.CreateSolutionAsync(solutionChecksum).ConfigureAwait(false));

                return s_primaryWorkspace.CurrentSolution;
            }

            // new solution. bulk sync all asset for the solution
            await _assetService.SynchronizeSolutionAssetsAsync(solutionChecksum, cancellationToken).ConfigureAwait(false);

            Debug.WriteLine("**** New Primary workspace!");
            s_primaryWorkspace.ClearSolution();
            s_primaryWorkspace.AddSolution(await updater.CreateSolutionInfoAsync(solutionChecksum).ConfigureAwait(false));

            return s_primaryWorkspace.CurrentSolution;
        }

        private static Solution GetAvailableSolution(Checksum solutionChecksum)
        {
            var currentSolution = s_primarySolution;
            if (currentSolution?.Item1 == solutionChecksum)
            {
                // asked about primary solution
                return currentSolution.Item2;
            }

            var lastSolution = s_lastSolution;
            if (lastSolution?.Item1 == solutionChecksum)
            {
                // asked about last solution
                return lastSolution.Item2;
            }

            return null;
        }
    }
}
