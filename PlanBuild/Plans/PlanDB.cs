﻿using Jotunn.Managers;
using PlanBuild.Blueprints;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEngine;
using Logger = Jotunn.Logger;
using Object = UnityEngine.Object;

namespace PlanBuild.Plans
{
    internal class PlanDB
    {
        private static PlanDB _instance;

        public static PlanDB Instance
        {
            get
            {
                return _instance ??= new PlanDB();
            }
        }

        /// <summary>
        ///     Checks if a piece table is valid for creating plan pieces from
        /// </summary>
        /// <param name="pieceTable"></param>
        /// <returns></returns>
        private static bool IsValidPieceTable(PieceTable pieceTable)
        {
            return pieceTable && !pieceTable.name.Equals(PlanHammerPrefab.PieceTableName) && !pieceTable.name.Equals(BlueprintAssets.PieceTableName);
        }

        public readonly Dictionary<string, Piece> PlanToOriginalMap = new Dictionary<string, Piece>();
        public readonly Dictionary<string, PlanPiecePrefab> PlanPiecePrefabs = new Dictionary<string, PlanPiecePrefab>();

        /// <summary>
        ///     Different pieces can have the same m_name (also across different mods), but m_knownRecipes is a HashSet, so can not handle duplicates well
        ///     This map keeps track of the duplicate mappings
        /// </summary>
        public Dictionary<string, List<Piece>> NamePiecePrefabMapping = new Dictionary<string, List<Piece>>();

        public void ScanPieceTables()
        {
            Logger.LogDebug("Scanning PieceTables for Pieces");
            PieceTable planPieceTable = PieceManager.Instance.GetPieceTable(PlanHammerPrefab.PieceTableName);

            var pieceTables = Resources.FindObjectsOfTypeAll<PieceTable>().Where(IsValidPieceTable);
            var currentPieces = new Dictionary<string, Piece>(); // cache valid pieces names from PieceTables

            // create plan pieces
            foreach (PieceTable pieceTable in pieceTables)
            {
                foreach (GameObject piecePrefab in pieceTable.m_pieces)
                {
                    if (!piecePrefab)
                    {
                        Logger.LogWarning($"Invalid prefab in {pieceTable.name} PieceTable");
                        continue;
                    }

                    Piece piece = piecePrefab.GetComponent<Piece>();
                    if (!piece)
                    {
                        Logger.LogWarning($"Prefab {piecePrefab.name} has no Piece?!");
                        continue;
                    }

                    try
                    {
                        if (piece.name == "piece_repair")
                        {
                            continue;
                        }
                        if (!CanCreatePlan(piece))
                        {
                            continue;
                        }
                        if (!EnsurePrefabRegistered(piece))
                        {
                            continue;
                        }

                        currentPieces.Add(piece.name, piece);

                        if (PlanPiecePrefabs.ContainsKey(piece.name))
                        {
                            continue;
                        }

                        PlanPiecePrefab planPiece = new PlanPiecePrefab(piece);
                        PrefabManager.Instance.RegisterToZNetScene(planPiece.PiecePrefab);
                        PlanToOriginalMap.Add(planPiece.PiecePrefab.name, planPiece.OriginalPiece);
                        PlanPiecePrefabs.Add(piece.name, planPiece);

                        if (!NamePiecePrefabMapping.TryGetValue(piece.m_name, out List<Piece> nameList))
                        {
                            nameList = new List<Piece>();
                            NamePiecePrefabMapping.Add(piece.m_name, nameList);
                        }
                        nameList.Add(piece);
                    }
                    catch (Exception e)
                    {
                        Logger.LogWarning($"Error while creating plan of {piece.name}: {e}");
                    }
                }
            }

            // Handle which plan pieces are shown in the PlanHammer piece table and update
            // existing plan pieces to reflect changes in original pieces
            foreach (string pieceName in PlanPiecePrefabs.Keys)
            {
                try
                {
                    var planPiece = PlanPiecePrefabs[pieceName];
                    if (currentPieces.TryGetValue(pieceName, out Piece orgPiece))
                    {
                        planPiece.Piece.m_enabled = orgPiece.m_enabled;
                        planPiece.Piece.m_icon = orgPiece.m_icon;
                        // update Icon for MVBP and WackyDB, or other mods
                        // that create icons when adding/creating pieces at runtime

                        if (orgPiece.m_enabled)
                        {
                            PieceManager.Instance.AddPiece(planPiece);
                            PieceManager.Instance.RegisterPieceInPieceTable(
                                planPiece.PiecePrefab,
                                planPiece.PieceTable,
                                planPiece.Category
                            );
                            continue;
                        }
                    }

                    planPiece.Piece.m_enabled = false;
                    if (planPieceTable.m_pieces.Contains(planPiece.PiecePrefab))
                    {
                        planPieceTable.m_pieces.Remove(planPiece.PiecePrefab);
                        PieceManager.Instance.RemovePiece(planPiece);
                    }
                }
                catch (Exception e)
                {
                    Logger.LogWarning($"Error while updating plan of {pieceName}: {e}");
                }
            }

            WarnDuplicatesWithDifferentResources();
        }

        /// <summary>
        /// Small wrapper class that implements equality and stable hashCode on Dictionary
        /// </summary>
        private class PieceRequirements
        {
            private readonly Dictionary<string, int> requirements;

            public PieceRequirements(Dictionary<string, int> requirements)
            {
                this.requirements = requirements;
            }

            public override int GetHashCode()
            {
                var hash = 13;
                var orderedKVPList = this.requirements.OrderBy(kvp => kvp.Key);
                foreach (var kvp in orderedKVPList)
                {
                    hash = (hash * 7) + kvp.Key.GetHashCode();
                    hash = (hash * 7) + kvp.Value.GetHashCode();
                }
                return hash;
            }

            public override bool Equals(object obj)
            {
                PieceRequirements other = obj as PieceRequirements;
                if (other == null)
                {
                    return false;
                }
                return this.requirements.Count == other.requirements.Count && !this.requirements.Except(other.requirements).Any();
            }

            public override string ToString()
            {
                return string.Join(", ", requirements.Select(x => x.Key + ":" + x.Value));
            }
        }

        private void WarnDuplicatesWithDifferentResources()
        {
            var warnDict = new Dictionary<string, IEnumerable<IGrouping<PieceRequirements, Piece>>>();
            foreach (var entry in NamePiecePrefabMapping)
            {
                List<Piece> pieces = entry.Value;
                if (pieces.Count == 1)
                {
                    continue;
                }

                var grouping = pieces.GroupBy(x => GetResourceMap(x));
                if (grouping.Count() > 1)
                {
                    warnDict[entry.Key] = grouping;
                }
            }

            if (warnDict.Any())
            {
                StringBuilder builder = new StringBuilder();
                builder.AppendLine("Warning for mod developers:\nMultiple pieces with the same m_name but different resource requirements, this will cause issues with Player.m_knownRecipes!");
                foreach (var entry in warnDict)
                {
                    builder.AppendLine($"Piece.m_name: {entry.Key}");
                    foreach (var groupEntry in entry.Value)
                    {
                        builder.AppendLine($" Requirements: {groupEntry.Key}");
                        builder.AppendLine($" Pieces: {string.Join(", ", groupEntry.Select(x => x.name))}\n");
                    }
                }
                Logger.LogWarning(builder.ToString());
            }
        }

        private PieceRequirements GetResourceMap(Piece y)
        {
            var result = new Dictionary<string, int>(y.m_resources.Length);
            foreach (Piece.Requirement req in y.m_resources)
            {
                result[req.m_resItem.m_itemData.m_shared.m_name] = req.m_amount;
            }
            return new PieceRequirements(result);
        }

        internal IEnumerable<PlanPiecePrefab> GetPlanPiecePrefabs()
        {
            return PlanPiecePrefabs.Values;
        }

        private bool EnsurePrefabRegistered(Piece piece)
        {
            GameObject prefab = PrefabManager.Instance.GetPrefab(piece.gameObject.name);
            if (prefab)
            {
                return true;
            }
            Logger.LogWarning("Piece " + piece.name + " in Hammer not fully registered? Could not find prefab " + piece.gameObject.name);
            if (!ZNetScene.instance.m_prefabs.Contains(piece.gameObject))
            {
                Logger.LogWarning(" Not registered in ZNetScene.m_prefabs! Adding now");
                ZNetScene.instance.m_prefabs.Add(piece.gameObject);
            }
            if (!ZNetScene.instance.m_namedPrefabs.ContainsKey(piece.gameObject.name.GetStableHashCode()))
            {
                Logger.LogWarning(" Not registered in ZNetScene.m_namedPrefabs! Adding now");
                ZNetScene.instance.m_namedPrefabs[piece.gameObject.name.GetStableHashCode()] = piece.gameObject;
            }
            //Prefab was added incorrectly, make sure the game doesn't delete it when logging out
            GameObject prefabParent = piece.gameObject.transform.parent?.gameObject;
            if (!prefabParent)
            {
                Logger.LogWarning(" Prefab has no parent?! Adding to Jotunn");
                PrefabManager.Instance.AddPrefab(piece.gameObject);
            }
            else if (prefabParent.scene.buildIndex != -1)
            {
                Logger.LogWarning(" Prefab container not marked as DontDestroyOnLoad! Marking now");
                Object.DontDestroyOnLoad(prefabParent);
            }
            return PrefabManager.Instance.GetPrefab(piece.gameObject.name) != null;
        }

        /// <summary>
        ///     Tries to find the vanilla piece from a plan prefab name
        /// </summary>
        /// <param name="name">Name of the plan prefab</param>
        /// <param name="originalPiece">Vanilla piece of the plan piece</param>
        /// <returns>true if a vanilla piece was found</returns>
        internal bool FindOriginalByPrefabName(string name, out Piece originalPiece)
        {
            return PlanToOriginalMap.TryGetValue(name, out originalPiece);
        }

        /// <summary>
        ///     Tries to find all vanilla pieces with a piece component name
        /// </summary>
        /// <param name="m_name">In-game name of the piece component</param>
        /// <param name="originalPieces">List of vanilla pieces with that piece name</param>
        /// <returns></returns>
        internal bool FindOriginalByPieceName(string m_name, out List<Piece> originalPieces)
        {
            return NamePiecePrefabMapping.TryGetValue(m_name, out originalPieces);
        }

        /// <summary>
        ///     Tries to find the plan prefab from a prefab name
        /// </summary>
        /// <param name="name">Name of the prefab</param>
        /// <param name="planPiecePrefab">Plan prefab</param>
        /// <returns>true if a plan prefab was found for that prefabs name</returns>
        internal bool FindPlanByPrefabName(string name, out PlanPiecePrefab planPiecePrefab)
        {
            int index = name.IndexOf("(Clone)", StringComparison.Ordinal);
            if (index != -1)
            {
                name = name.Substring(0, index);
            }
            return PlanPiecePrefabs.TryGetValue(name, out planPiecePrefab);
        }

        public bool CanCreatePlan(Piece piece)
        {
            return piece.GetComponent<Plant>() == null
                   && piece.GetComponent<TerrainOp>() == null
                   && piece.GetComponent<TerrainModifier>() == null
                   && piece.GetComponent<Ship>() == null
                   && piece.GetComponent<PlanPiece>() == null
                   && !piece.name.Equals(PlanTotemPrefab.PlanTotemPieceName);
        }
    }
}
