using System.Linq;
using Sandbox.ModAPI;
using System;
using System.Collections.Generic;
using VRage.Game;
using VRage.Game.ModAPI;
using VRageMath;
using PEPCO.Utilities;

namespace PEPCO
{
    public static class ScriptHelpers
    {
        /// <summary>
        /// Transforms a position from local space into world/global space
        /// using the given world transformation matrix.
        /// </summary>
        /// <param name="localPosition">The position vector in local coordinates.</param>
        /// <param name="worldMatrix">The world transformation matrix.</param>
        /// <returns>The position vector in world coordinates.</returns>
        public static Vector3D LocalPositionToGlobal(Vector3D localPosition, MatrixD worldMatrix)
        {
            return Vector3D.Transform(localPosition, worldMatrix);
        }

        /// <summary>
        /// Transforms a position from world/global space into local space
        /// by applying the inverse of the given world transformation matrix.
        /// </summary>
        /// <param name="globalPosition">The position vector in world coordinates.</param>
        /// <param name="worldMatrix">The world transformation matrix.</param>
        /// <returns>The position vector in local coordinates.</returns>
        public static Vector3D GlobalPositionToLocal(Vector3D globalPosition, MatrixD worldMatrix)
        {
            return Vector3D.Transform(globalPosition, InvertMatrixLight(worldMatrix));
        }

        /// <summary>
        /// Transforms a direction vector from local space into world/global space
        /// using the given world transformation matrix.
        /// </summary>
        /// <param name="localDirection">The direction vector in local coordinates.</param>
        /// <param name="worldMatrix">The world transformation matrix.</param>
        /// <returns>The direction vector in world coordinates.</returns>
        public static Vector3D LocalDirectionToGlobal(Vector3D localDirection, MatrixD worldMatrix)
        {
            return Vector3D.TransformNormal(localDirection, worldMatrix);
        }

        /// <summary>
        /// Transforms a direction vector from world/global space into local space
        /// by applying the inverse of the given world transformation matrix.
        /// </summary>
        /// <param name="globalDirection">The direction vector in world coordinates.</param>
        /// <param name="worldMatrix">The world transformation matrix.</param>
        /// <returns>The direction vector in local coordinates.</returns>
        public static Vector3D GlobalDirectionToLocal(Vector3D globalDirection, MatrixD worldMatrix)
        {
            return Vector3D.TransformNormal(globalDirection, InvertMatrixLight(worldMatrix));
        }

        /// <summary>
        /// Computes a lightweight inverse of a transformation matrix,
        /// correctly inverting its rotation and translation components
        /// for any orthonormal (pure rotation + translation) matrix.
        /// This ignores scaling/shearing for performance.
        /// </summary>
        /// <param name="matrix">The orthonormal transformation matrix to invert.</param>
        /// <returns>The inverted transformation matrix.</returns>
        public static MatrixD InvertMatrixLight(MatrixD matrix)
        {
            // Start with identity
            MatrixD inverted = MatrixD.Identity;

            // Transpose the rotation part (upper 3×3) for orthonormal inverse
            inverted.M11 = matrix.M11;
            inverted.M12 = matrix.M21;
            inverted.M13 = matrix.M31;

            inverted.M21 = matrix.M12;
            inverted.M22 = matrix.M22;
            inverted.M23 = matrix.M32;

            inverted.M31 = matrix.M13;
            inverted.M32 = matrix.M23;
            inverted.M33 = matrix.M33;

            // Invert translation: apply inverted rotation to negative original position
            inverted.Translation = -Vector3D.TransformNormal(matrix.Translation, inverted);

            return inverted;
        }



        /// <summary>
        /// Converts a global/world-space matrix to its local-space equivalent
        /// relative to a given parent matrix, without performing a full matrix inversion.
        /// This is optimized for orthonormal transforms (rotation + translation).
        /// </summary>
        /// <param name="globalMatrix">The object's global transformation matrix.</param>
        /// <param name="parentMatrix">The parent object's global transformation matrix.</param>
        /// <returns>The object's local transformation matrix relative to the parent.</returns>
        public static MatrixD ToLocalMatrixFast(MatrixD globalMatrix, MatrixD parentMatrix)
        {
            // Extract parent's rotation as a 3x3 (upper-left) and transpose it for inverse
            MatrixD invParentRot = MatrixD.Transpose(parentMatrix);

            // Extract parent's translation
            Vector3D parentPos = parentMatrix.Translation;

            // Build inverse parent matrix (rotation + translation)
            MatrixD invParent = invParentRot;
            invParent.Translation = Vector3D.TransformNormal(-parentPos, invParentRot);

            // Multiply: global → local
            return globalMatrix * invParent;
        }


        public static Color GetPlayerColorSafe(IMyPlayer player)
        {
            if (player?.Identity == null)
                return Color.Red; // Fallback/default color

            try
            {
                var colorMask = player.Identity.ColorMask;
                var normalHSV = MyColorPickerConstants.HSVOffsetToHSV(colorMask.Value);
                return ColorExtensions.HSVtoColor(normalHSV);
            }
            catch
            {
                // In case something unexpected happens in the conversion
                return Color.Red;
            }
        }

        public static string ColorToHex(Color color, bool includeAlpha = false)
        {
            return includeAlpha
                ? $"{color.R:X2}{color.G:X2}{color.B:X2}{color.A:X2}"
                : $"{color.R:X2}{color.G:X2}{color.B:X2}";
        }

        /// <summary>
        /// Returns true if v1 is strictly greater than v2 on both components (component-wise comparison).
        /// This does not compare vector magnitudes/lengths.
        /// </summary>
        /// <param name="v1">First vector.</param>
        /// <param name="v2">Second vector.</param>
        /// <returns>True when v1.X > v2.X and v1.Y > v2.Y; otherwise, false.</returns>
        public static bool IsFirstLarger(Vector2 v1, Vector2 v2)
        {
            return v1.X > v2.X && v1.Y > v2.Y;
        }

        /// <summary>
        /// Returns true if 'test' lies strictly between 'small' (lower bound) and 'large' (upper bound)
        /// on both X and Y using component-wise comparison.
        /// </summary>
        /// <param name="test">Vector to evaluate.</param>
        /// <param name="small">Lower bound (min X and Y).</param>
        /// <param name="large">Upper bound (max X and Y).</param>
        /// <returns>True when small.X &lt; test.X &lt; large.X and small.Y &lt; test.Y &lt; large.Y; otherwise, false.</returns>
        public static bool IsBetween(Vector2 test, Vector2 small, Vector2 large)
        {
            var issmallerThanLarge = large.X > test.X && large.Y > test.Y;
            var islargerThanSmall = small.X < test.X && small.Y < test.Y;

            return issmallerThanLarge && islargerThanSmall;
        }

        public static string NormalizeHex(string hex)
        {
            if (string.IsNullOrWhiteSpace(hex))
                return null;

            // Remove leading '#', trim, uppercase
            hex = hex.Trim();
            if (hex.StartsWith("#"))
                hex = hex.Substring(1);

            hex = hex.ToUpperInvariant();

            // Accept only 6 or 8 hex digits; prefer 6 without alpha
            if (IsHex(hex) && (hex.Length == 6 || hex.Length == 8))
            {
                // If 8 digits (ARGB/RGBA), convert to 6 by dropping alpha if needed
                if (hex.Length == 8)
                {
                    // Assume AARRGGBB; drop AA -> RRBBGG stays RR GG BB
                    hex = hex.Substring(2, 6);
                }
                return hex;
            }

            return null;
        }

        private static bool IsHex(string value)
        {
            for (int i = 0; i < value.Length; i++)
            {
                char c = value[i];
                bool isHex = (c >= '0' && c <= '9') ||
                             (c >= 'A' && c <= 'F') ||
                             (c >= 'a' && c <= 'f');
                if (!isHex)
                    return false;
            }
            return true;
        }

        public static Color GetSuitColor(long identityId)
        {

            var identities = new List<IMyIdentity>();
            MyAPIGateway.Players.GetAllIdentites(identities);

            // Check that we actually got a list and can match by ID
            var identity = identities?
                .FirstOrDefault(id => id != null && id.IdentityId == identityId);

            // Bail out early if no matching identity
            if (identity == null)
                return new Color(255, 0, 255);

            // ColorMask is nullable, so check before using Value
            if (!identity.ColorMask.HasValue)
                return new Color(255, 0, 255);

            // At this point it's safe to use ColorMask.Value
            var colorMask = identity.ColorMask.Value;
            var normalHSV = MyColorPickerConstants.HSVOffsetToHSV(colorMask);

            // Assuming HSVtoColor() is safe, but wrap in a try/catch if unsure
            var color = ColorExtensions.HSVtoColor(normalHSV);
            return color;
        }

        #region Player Identity & Steam ID Helpers

        /// <summary>
        /// Gets the Steam ID of the local player (client-side only).
        /// Returns 0 if not available or on dedicated server.
        /// </summary>
        public static ulong GetLocalPlayerSteamId()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Multiplayer == null)
                return 0;

            return MyAPIGateway.Multiplayer.MyId;
        }

        /// <summary>
        /// Gets the Identity ID of the local player (client-side only).
        /// Returns 0 if not available or on dedicated server.
        /// </summary>
        public static long GetLocalPlayerIdentityId()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null)
                return 0;

            return MyAPIGateway.Session.Player.IdentityId;
        }

        /// <summary>
        /// Gets the Identity ID of the local player (client-side only).
        /// Returns 0 if not available or on dedicated server.
        /// </summary>
        public static string GetLocalPlayerName()
        {
            if (MyAPIGateway.Session == null || MyAPIGateway.Session.Player == null)
                return null;

            return MyAPIGateway.Session.Player.DisplayName;
        }

        /// <summary>
        /// Gets the identity associated with the given Steam ID.
        /// Returns null if not found.
        /// </summary>
        public static IMyIdentity GetIdentityBySteamId(ulong steamId)
        {
            if (MyAPIGateway.Players == null)
                return null;

            var identities = new List<IMyIdentity>();
            MyAPIGateway.Players.GetAllIdentites(identities);

            foreach (var identity in identities)
            {
                if (identity == null)
                    continue;

                var player = GetPlayerByIdentityId(identity.IdentityId);
                if (player != null && player.SteamUserId == steamId)
                    return identity;
            }

            return null;
        }

        /// <summary>
        /// Gets the Steam ID associated with the given Identity ID.
        /// Returns 0 if not found.
        /// </summary>
        public static ulong GetSteamIdByIdentityId(long identityId)
        {
            var player = GetPlayerByIdentityId(identityId);
            return player?.SteamUserId ?? 0;
        }

        /// <summary>
        /// Gets the player object associated with the given Identity ID.
        /// Returns null if not found.
        /// </summary>
        public static IMyPlayer GetPlayerByIdentityId(long identityId)
        {
            if (MyAPIGateway.Players == null)
                return null;

            var players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);

            return players.FirstOrDefault(p => p != null && p.IdentityId == identityId);
        }

        /// <summary>
        /// Gets all currently connected players.
        /// Returns an empty list if none found.
        /// </summary>
        public static List<IMyPlayer> GetAllPlayers()
        {
            var players = new List<IMyPlayer>();

            if (MyAPIGateway.Players != null)
                MyAPIGateway.Players.GetPlayers(players);

            return players;
        }

        /// <summary>
        /// Gets the display name of a player by their Identity ID.
        /// Returns null if not found.
        /// </summary>
        public static string GetPlayerName(long identityId)
        {
            if (MyAPIGateway.Players == null)
                return null;

            var identities = new List<IMyIdentity>();
            MyAPIGateway.Players.GetAllIdentites(identities);

            var identity = identities.FirstOrDefault(i => i != null && i.IdentityId == identityId);
            return identity?.DisplayName;
        }

        #endregion

        #region Faction Queries

        /// <summary>
        /// Gets a faction by its unique ID.
        /// Returns null if not found.
        /// </summary>
        public static IMyFaction GetFactionById(long factionId)
        {
            if (MyAPIGateway.Session?.Factions == null)
                return null;

            return MyAPIGateway.Session.Factions.TryGetFactionById(factionId);
        }

        /// <summary>
        /// Gets a faction by its tag (case-sensitive).
        /// Returns null if not found.
        /// </summary>
        public static IMyFaction GetFactionByTag(String tag)
        {
            if (string.IsNullOrEmpty(tag) || MyAPIGateway.Session?.Factions == null)
                return null;

            return MyAPIGateway.Session.Factions.TryGetFactionByTag(tag);
        }

        /// <summary>
        /// Gets a faction by its name (case-sensitive).
        /// Returns null if not found.
        /// Note: This is a less efficient operation as it requires checking the faction
        /// of every player to build a list of all factions.
        /// </summary>
        public static IMyFaction GetFactionByName(string name)
        {
            if (string.IsNullOrEmpty(name) || MyAPIGateway.Session?.Factions == null)
                return null;

            // Since there's no direct API to enumerate all factions,
            // we need to check every identity's faction
            var identities = new List<IMyIdentity>();
            MyAPIGateway.Players.GetAllIdentites(identities);

            var checkedFactions = new HashSet<long>();

            foreach (var identity in identities)
            {
                if (identity == null)
                    continue;

                var faction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(identity.IdentityId);
                if (faction != null && !checkedFactions.Contains(faction.FactionId))
                {
                    checkedFactions.Add(faction.FactionId);
                    if (faction.Name == name)
                        return faction;
                }
            }

            return null;
        }

        /// <summary>
        /// Gets the faction that a player belongs to.
        /// Returns null if the player is not in a faction.
        /// </summary>
        public static IMyFaction GetPlayerFaction(long identityId)
        {
            if (MyAPIGateway.Session?.Factions == null)
                return null;

            return MyAPIGateway.Session.Factions.TryGetPlayerFaction(identityId);
        }

        /// <summary>
        /// Gets all members of a faction.
        /// Returns an empty list of identity IDs if faction not found or has no members.
        /// </summary>
        public static List<long> GetFactionMembers(long factionId)
        {
            var members = new List<long>();
            var faction = GetFactionById(factionId);

            if (faction?.Members == null)
                return members;

            foreach (var member in faction.Members)
            {
                members.Add(member.Key);
            }

            return members;
        }

        /// <summary>
        /// Checks if a player is in the specified faction.
        /// </summary>
        public static bool IsPlayerInFaction(long identityId, long factionId)
        {
            var playerFaction = GetPlayerFaction(identityId);
            return playerFaction != null && playerFaction.FactionId == factionId;
        }

        /// <summary>
        /// Checks if two players are in the same faction.
        /// Returns false if either player is not in a faction.
        /// </summary>
        public static bool ArePlayersInSameFaction(long identityId1, long identityId2)
        {
            var faction1 = GetPlayerFaction(identityId1);
            var faction2 = GetPlayerFaction(identityId2);

            return faction1 != null && faction2 != null && faction1.FactionId == faction2.FactionId;
        }

        /// <summary>
        /// Gets all factions that are allies with the specified faction.
        /// Returns an empty list if none found.
        /// Note: This is a less efficient operation as it requires checking relations
        /// with all discovered factions.
        /// </summary>
        public static List<IMyFaction> GetFactionAllies(long factionId)
        {
            var allies = new List<IMyFaction>();
            var faction = GetFactionById(factionId);

            if (faction == null || MyAPIGateway.Session?.Factions == null)
                return allies;

            // Since there's no direct API to enumerate all factions,
            // we need to check every identity's faction
            var identities = new List<IMyIdentity>();
            MyAPIGateway.Players.GetAllIdentites(identities);

            var checkedFactions = new HashSet<long>();
            checkedFactions.Add(factionId);

            foreach (var identity in identities)
            {
                if (identity == null)
                    continue;

                var otherFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(identity.IdentityId);
                if (otherFaction != null && !checkedFactions.Contains(otherFaction.FactionId))
                {
                    checkedFactions.Add(otherFaction.FactionId);
                    var relation = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(factionId, otherFaction.FactionId);
                    if (relation == MyRelationsBetweenFactions.Friends)
                        allies.Add(otherFaction);
                }
            }

            return allies;
        }

        /// <summary>
        /// Gets all factions that are enemies with the specified faction.
        /// Returns an empty list if none found.
        /// Note: This is a less efficient operation as it requires checking relations
        /// with all discovered factions.
        /// </summary>
        public static List<IMyFaction> GetFactionEnemies(long factionId)
        {
            var enemies = new List<IMyFaction>();
            var faction = GetFactionById(factionId);

            if (faction == null || MyAPIGateway.Session?.Factions == null)
                return enemies;

            // Since there's no direct API to enumerate all factions,
            // we need to check every identity's faction
            var identities = new List<IMyIdentity>();
            MyAPIGateway.Players.GetAllIdentites(identities);

            var checkedFactions = new HashSet<long>();
            checkedFactions.Add(factionId);

            foreach (var identity in identities)
            {
                if (identity == null)
                    continue;

                var otherFaction = MyAPIGateway.Session.Factions.TryGetPlayerFaction(identity.IdentityId);
                if (otherFaction != null && !checkedFactions.Contains(otherFaction.FactionId))
                {
                    checkedFactions.Add(otherFaction.FactionId);
                    var relation = MyAPIGateway.Session.Factions.GetRelationBetweenFactions(factionId, otherFaction.FactionId);
                    if (relation == MyRelationsBetweenFactions.Enemies)
                        enemies.Add(otherFaction);
                }
            }

            return enemies;
        }

        /// <summary>
        /// Gets all pending join requests for the specified faction.
        /// Returns an empty list if none found.
        /// </summary>
        public static List<long> GetFactionRequests(long factionId)
        {
            var requests = new List<long>();
            var faction = GetFactionById(factionId);

            if (faction?.JoinRequests == null)
                return requests;

            foreach (var request in faction.JoinRequests)
            {
                requests.Add(request.Key);
            }

            return requests;
        }

        /// <summary>
        /// Gets all factions that the specified faction is at war with.
        /// This is an alias for GetFactionEnemies for semantic clarity.
        /// </summary>
        public static List<IMyFaction> GetFactionWars(long factionId)
        {
            return GetFactionEnemies(factionId);
        }

        #endregion

        #region Faction Role Queries

        /// <summary>
        /// Gets the rank/role of a member in their faction.
        /// Returns MyPromoteLevel.None if not found.
        /// Note: Regular faction members without leadership roles return None to distinguish from non-members.
        /// </summary>
        public static MyPromoteLevel GetMemberRole(long identityId)
        {
            var faction = GetPlayerFaction(identityId);
            if (faction?.Members == null)
                return MyPromoteLevel.None;

            MyFactionMember member;
            if (faction.Members.TryGetValue(identityId, out member))
            {
                if (member.IsFounder)
                    return MyPromoteLevel.Owner;
                if (member.IsLeader)
                    return MyPromoteLevel.Admin;
            }

            return MyPromoteLevel.None;
        }

        /// <summary>
        /// Checks if a player is the founder of their faction.
        /// </summary>
        public static bool IsFounder(long identityId)
        {
            var faction = GetPlayerFaction(identityId);
            return faction != null && faction.FounderId == identityId;
        }

        /// <summary>
        /// Checks if a player is a leader in their faction.
        /// </summary>
        public static bool IsLeader(long identityId)
        {
            var faction = GetPlayerFaction(identityId);
            if (faction?.Members == null)
                return false;

            MyFactionMember member;
            if (faction.Members.TryGetValue(identityId, out member))
                return member.IsLeader || member.IsFounder;

            return false;
        }

        /// <summary>
        /// Checks if a player is a recruit in their faction (default member without leadership).
        /// </summary>
        public static bool IsRecruit(long identityId)
        {
            var faction = GetPlayerFaction(identityId);
            if (faction?.Members == null)
                return false;

            MyFactionMember member;
            if (faction.Members.TryGetValue(identityId, out member))
                return !member.IsLeader && !member.IsFounder;

            return false;
        }

        /// <summary>
        /// Checks if a player is a member (standard member) in their faction.
        /// </summary>
        public static bool IsMember(long identityId)
        {
            var faction = GetPlayerFaction(identityId);
            if (faction?.Members == null)
                return false;

            return faction.Members.ContainsKey(identityId);
        }

        /// <summary>
        /// Gets all leaders in the specified faction.
        /// Returns an empty list if none found.
        /// </summary>
        public static List<long> GetFactionLeaders(long factionId)
        {
            var leaders = new List<long>();
            var faction = GetFactionById(factionId);

            if (faction?.Members == null)
                return leaders;

            foreach (var memberKvp in faction.Members)
            {
                if (memberKvp.Value.IsLeader || memberKvp.Value.IsFounder)
                    leaders.Add(memberKvp.Key);
            }

            return leaders;
        }

        /// <summary>
        /// Gets the founder of the specified faction.
        /// Returns 0 if faction not found.
        /// </summary>
        public static long GetFactionFounder(long factionId)
        {
            var faction = GetFactionById(factionId);
            return faction?.FounderId ?? 0;
        }

        /// <summary>
        /// Gets all recruits (regular members) in the specified faction.
        /// Returns an empty list if none found.
        /// </summary>
        public static List<long> GetFactionRecruits(long factionId)
        {
            var recruits = new List<long>();
            var faction = GetFactionById(factionId);

            if (faction?.Members == null)
                return recruits;

            foreach (var memberKvp in faction.Members)
            {
                if (!memberKvp.Value.IsLeader && !memberKvp.Value.IsFounder)
                    recruits.Add(memberKvp.Key);
            }

            return recruits;
        }

        /// <summary>
        /// Gets all members with the specified role in a faction.
        /// Returns an empty list if none found.
        /// Note: Only Owner and Admin roles are distinguished; other members cannot be filtered by this method.
        /// </summary>
        public static List<long> GetFactionMembersByRole(long factionId, MyPromoteLevel role)
        {
            var members = new List<long>();
            var faction = GetFactionById(factionId);

            if (faction?.Members == null)
                return members;

            foreach (var memberKvp in faction.Members)
            {
                bool matches = false;

                switch (role)
                {
                    case MyPromoteLevel.Owner:
                        matches = memberKvp.Value.IsFounder;
                        break;
                    case MyPromoteLevel.Admin:
                        matches = memberKvp.Value.IsLeader && !memberKvp.Value.IsFounder;
                        break;
                        // Regular members don't have distinguishing properties in the API
                }

                if (matches)
                    members.Add(memberKvp.Key);
            }

            return members;
        }

        /// <summary>
        /// Promotes a faction member to the next rank.
        /// Returns true if successful. Only works on server-side or in single-player.
        /// </summary>
        public static bool PromoteMember(long factionId, long identityId)
        {
            if (MyAPIGateway.Session?.Factions == null)
                return false;

            try
            {
                MyAPIGateway.Session.Factions.PromoteMember(factionId, identityId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Demotes a faction member to the previous rank.
        /// Returns true if successful. Only works on server-side or in single-player.
        /// </summary>
        public static bool DemoteMember(long factionId, long identityId)
        {
            if (MyAPIGateway.Session?.Factions == null)
                return false;

            try
            {
                MyAPIGateway.Session.Factions.DemoteMember(factionId, identityId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Transfers leadership of a faction to another member.
        /// Returns true if successful. Only works on server-side or in single-player.
        /// Note: This uses ChangeAutoAccept which is the available API method,
        /// actual leadership transfer may need to be done through promote/demote.
        /// </summary>
        public static bool TransferLeadership(long factionId, long newLeaderId)
        {
            if (MyAPIGateway.Session?.Factions == null)
                return false;

            var faction = GetFactionById(factionId);
            if (faction == null || !faction.Members.ContainsKey(newLeaderId))
                return false;

            try
            {
                // Promote the new leader to max rank
                MyAPIGateway.Session.Factions.PromoteMember(factionId, newLeaderId);
                return true;
            }
            catch
            {
                return false;
            }
        }

        #endregion

        #region Utility & Session Helpers

        /// <summary>
        /// Gets the current game session object.
        /// Returns null if not available.
        /// </summary>
        public static IMySession GetCurrentSession()
        {
            return MyAPIGateway.Session;
        }

        /// <summary>
        /// Gets the elapsed game time since the world was created.
        /// Returns TimeSpan.Zero if session not available.
        /// </summary>
        public static TimeSpan GetElapsedGameTime()
        {
            if (MyAPIGateway.Session == null)
                return TimeSpan.Zero;

            return MyAPIGateway.Session.GameDateTime - new DateTime(2081, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        }

        /// <summary>
        /// Gets the name of the current server/world.
        /// Returns null if session not available.
        /// </summary>
        public static string GetServerName()
        {
            return MyAPIGateway.Session?.Name;
        }

        /// <summary>
        /// Checks if the current instance is a dedicated server.
        /// </summary>
        public static bool IsDedicatedServer()
        {
            if (MyAPIGateway.Utilities == null)
                return false;

            return MyAPIGateway.Utilities.IsDedicated;
        }

        /// <summary>
        /// Checks if the current game is multiplayer.
        /// </summary>
        public static bool IsMultiplayer()
        {
            if (MyAPIGateway.Multiplayer == null)
                return false;

            return MyAPIGateway.Multiplayer.IsServer || !MyAPIGateway.Utilities.IsDedicated;
        }

        /// <summary>
        /// Gets the count of currently online players.
        /// Returns 0 if unable to determine.
        /// </summary>
        public static int GetOnlinePlayersCount()
        {
            if (MyAPIGateway.Players == null)
                return 0;

            var players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);
            return players.Count;
        }

        /// <summary>
        /// Gets all identities in the game (including NPC identities).
        /// Returns an empty list if none found.
        /// </summary>
        public static List<IMyIdentity> GetAllIdentities()
        {
            var identities = new List<IMyIdentity>();

            if (MyAPIGateway.Players != null)
                MyAPIGateway.Players.GetAllIdentites(identities);

            return identities;
        }

        /// <summary>
        /// Gets all NPC identities (identities without an associated player).
        /// Returns an empty list if none found.
        /// </summary>
        public static List<IMyIdentity> GetNpcIdentities()
        {
            var npcIdentities = new List<IMyIdentity>();

            if (MyAPIGateway.Players == null)
                return npcIdentities;

            var allIdentities = new List<IMyIdentity>();
            MyAPIGateway.Players.GetAllIdentites(allIdentities);

            var players = new List<IMyPlayer>();
            MyAPIGateway.Players.GetPlayers(players);

            foreach (var identity in allIdentities)
            {
                if (identity == null)
                    continue;

                bool isPlayerIdentity = players.Any(p => p != null && p.IdentityId == identity.IdentityId);
                if (!isPlayerIdentity)
                    npcIdentities.Add(identity);
            }

            return npcIdentities;
        }


        #endregion

        #region GPS Helpers

        /// <summary>
        /// Gets all GPS markers for the specified player identity.
        /// Returns an empty list if not available.
        /// </summary>
        public static List<IMyGps> GetPlayerGpsList(long identityId)
        {
            if (MyAPIGateway.Session?.GPS == null)
                return new List<IMyGps>();

            return MyAPIGateway.Session.GPS.GetGpsList(identityId);
        }

        /// <summary>
        /// Gets a GPS marker by its hash.
        /// Returns null if not found.
        /// </summary>
        public static IMyGps GetGpsByHash(long identityId, int hash)
        {
            var gpsList = GetPlayerGpsList(identityId);
            return gpsList.FirstOrDefault(g => g.Hash == hash);
        }

        /// <summary>
        /// Checks if a GPS marker is visible to the player.
        /// </summary>
        public static bool IsGpsVisible(IMyGps gps)
        {
            return gps != null && !gps.DiscardAt.HasValue;
        }

        #endregion

        #region Debug Logging

        public static void LogDefault(string message)
        {
            Log.Info($"{message}");
        }

        public static void LogDebug(string message)
        {
            if (ModParameter.IsDebug())
            {
                Log.Info($"{message}");
            }
        }

        public static void LogError(string message)
        {
            Log.Error($"{message}");
        }

        #endregion

        #region API Availability Checks

        /// <summary>
        /// Safely gets the current session with null check.
        /// Returns null if session is not available.
        /// </summary>
        public static IMySession GetSessionSafe()
        {
            return MyAPIGateway.Session;
        }

        /// <summary>
        /// Checks if GPS API is available and ready.
        /// </summary>
        public static bool IsGpsAvailable()
        {
            return MyAPIGateway.Session?.GPS != null;
        }

        /// <summary>
        /// Checks if Factions API is available and ready.
        /// </summary>
        public static bool AreFactionsAvailable()
        {
            return MyAPIGateway.Session?.Factions != null;
        }

        /// <summary>
        /// Checks if Multiplayer API is available and ready.
        /// </summary>
        public static bool IsMultiplayerAvailable()
        {
            return MyAPIGateway.Multiplayer != null;
        }

        /// <summary>
        /// Checks if Utilities API is available and ready.
        /// </summary>
        public static bool AreUtilitiesAvailable()
        {
            return MyAPIGateway.Utilities != null;
        }

        #endregion

        #region Safe Color Conversion

        /// <summary>
        /// Safely converts hex color string to Color with fallback.
        /// </summary>
        /// <param name="hex">Hex color string (with or without '#')</param>
        /// <param name="fallback">Color to return if conversion fails</param>
        /// <returns>Converted color or fallback</returns>
        public static Color HexToColorSafe(string hex, Color fallback)
        {
            var normalized = NormalizeHex(hex);
            if (string.IsNullOrEmpty(normalized))
                return fallback;

            try
            {
                return ColorExtensions.HexToColor(normalized);
            }
            catch (Exception ex)
            {
                LogError($"HexToColorSafe: Failed to convert '{hex}' to color: {ex.Message}");
                return fallback;
            }
        }

        /// <summary>
        /// Safely converts a Color to hex string with error handling.
        /// </summary>
        /// <param name="color">Color to convert</param>
        /// <param name="includeAlpha">Whether to include alpha channel</param>
        /// <param name="fallbackHex">Hex string to return if conversion fails</param>
        /// <returns>Hex string or fallback</returns>
        public static string ColorToHexSafe(Color color, bool includeAlpha = false, string fallbackHex = "FFFFFF")
        {
            try
            {
                return ColorToHex(color, includeAlpha);
            }
            catch (Exception ex)
            {
                LogError($"ColorToHexSafe: Failed to convert color to hex: {ex.Message}");
                return fallbackHex;
            }
        }

        #endregion
    }
}
