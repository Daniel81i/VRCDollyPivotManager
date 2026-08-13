using System.Collections.Generic;
using System.Linq;
using System.Text;
using UnityEditor;
using UnityEngine;

namespace Daniel81i.VRChatCamDolly.EditorTools
{
    /// <summary>
    /// 選択中のオブジェクト配下を数え上げて、アバターの制限に効く項目を出す。
    ///
    /// VRChat の Total Max Particles は「実際に出ている粒の数」ではなく
    /// 各 ParticleSystem の maxParticles の合計。どれが枠を食っているかは
    /// SDK の表示からは分からないので、内訳をここで出す。
    ///
    /// 検証用。配布物には含めていない。
    /// </summary>
    internal static class CamDroneBudgetReport
    {
        private const string MenuPath = "Tools/CamDrone/Report Budget";

        [MenuItem(MenuPath, true)]
        private static bool Validate() => Selection.activeGameObject != null;

        [MenuItem(MenuPath, false, 5)]
        private static void Run()
        {
            var root = Selection.activeGameObject;
            if (root == null) return;

            var sb = new StringBuilder();
            sb.AppendLine($"[{root.name}] の内訳");
            sb.AppendLine();

            // -- パーティクル -------------------------------------------------
            var systems = root.GetComponentsInChildren<ParticleSystem>(true);
            var total = 0;
            var rows = new List<(string Path, int Max)>();
            foreach (var ps in systems)
            {
                var max = ps.main.maxParticles;
                total += max;
                rows.Add((PathOf(ps.transform, root.transform), max));
            }

            sb.AppendLine($"ParticleSystem   {systems.Length} 個");
            sb.AppendLine($"Total Max Particles   {total}");
            foreach (var r in rows.OrderByDescending(r => r.Max))
            {
                sb.AppendLine($"  {r.Max,8}  {r.Path}");
            }

            // -- そのほか制限に効くもの ---------------------------------------
            sb.AppendLine();
            sb.AppendLine("コンポーネント数（制限に関係しそうなものだけ）");
            var counts = new SortedDictionary<string, int>();
            foreach (var c in root.GetComponentsInChildren<Component>(true))
            {
                if (c == null) continue;
                var name = c.GetType().Name;
                if (!name.Contains("Raycast") && !name.Contains("Constraint")
                    && name != "SkinnedMeshRenderer" && name != "MeshRenderer") continue;
                counts.TryGetValue(name, out var n);
                counts[name] = n + 1;
            }

            foreach (var kv in counts) sb.AppendLine($"  {kv.Value,8}  {kv.Key}");

            Debug.Log(sb.ToString(), root);
        }

        private static string PathOf(Transform t, Transform root)
        {
            var parts = new List<string>();
            for (var c = t; c != null && c != root; c = c.parent) parts.Add(c.name);
            parts.Reverse();
            return string.Join("/", parts);
        }
    }
}
