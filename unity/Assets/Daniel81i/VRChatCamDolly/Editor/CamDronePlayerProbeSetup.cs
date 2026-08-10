using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Daniel81i.VRChatCamDolly.EditorTools
{
    /// <summary>
    /// A案（プレイヤープローブ）のセットアップ。
    ///
    /// FloorPointer の Object_1〜Object_5（ワールド固定されるスロット）それぞれに
    /// 「自分のプレイヤーコライダーへ向けた VRCRaycast」を仕込み、
    /// 固定点から自分の身体までの距離を Expression Parameter として OSC に出す。
    ///
    /// これは「自分のレイキャストが自分自身のプレイヤーコライダーに当たるか」を
    /// 実機で確かめるための検証用。当たるなら方位センサーを足して相対位置に拡張し、
    /// 当たらないなら Contact 方式（B案）へ切り替える。
    ///
    /// 生成物はすべてシーン上のアバターインスタンス配下のオーバーライドとして作られる。
    /// アバターや FloorPointer の prefab アセット本体には一切書き込まない。
    /// </summary>
    internal static class CamDronePlayerProbeSetup
    {
        private const string MenuPath = "Tools/CamDrone/Setup Player Probe (Plan A)";
        private const string RemoveMenuPath = "Tools/CamDrone/Remove Player Probe";

        private const string ControllerPath =
            "Assets/Daniel81i/VRChatCamDolly/Animator/CamDroneProbe_FX.controller";

        private const string ParamPrefix = "CamDrone/Probe";
        private const string ProbeRootName = "CamDrone Probe";
        private const string AimTargetName = "ProbeAimTarget";
        private const string RigChildName = "ProbeRig";
        private const string ResultChildName = "Result";
        private const string FloorPointerName = "FloorPointer";

        private const int SlotCount = 5;

        /// <summary>
        /// レイの最大距離(m)。_Ratio はこの値で正規化された 0〜1 で届く。
        ///
        /// _Distance は生のメートル値で届くので、伸ばしても精度は落ちない。
        /// 一方で短いと、視線方向にずれている原点（A から Baseline だけ遠い側）の
        /// レイが先に届かなくなり、その点が3本揃わず解けなくなる。
        /// 実測では 10m のとき、9.45m の固定点で C のレイ（要 10.95m）が外れた。
        /// 必要な長さの目安は「測りたい最大距離 + Baseline」。
        ///
        /// ただし測れる距離が伸びても精度は伸びない。誤差は「距離 ÷ Baseline」に
        /// 比例するので、実測の系統誤差 33mm では 10m で ±0.22m、50m で ±1.1m になる。
        /// </summary>
        private const float MaxDistance = 25f;

        /// <summary>
        /// レイが当たるレイヤー。VRChat では 9=Player（他プレイヤー）、10=PlayerLocal（自分）。
        ///
        /// collisionMode を HitPlayers にすると両方に当たるため、間に人が入ると
        /// 他人までの距離を測ってしまう。HitCustomLayers で PlayerLocal だけを
        /// 指定して自分専用にする。ワールド形状も除外されるので、壁越しでも測れる。
        ///
        /// 実機で _Hit が立たなくなった場合は自分のコライダーが別レイヤーに
        /// いるということなので、"Player" に変えて試すこと。
        /// </summary>
        private const string SelfCollisionLayer = "PlayerLocal";

        /// <summary>
        /// 三辺測量の基線長(m)。ProbeRig のローカル座標で
        /// A=(0,0,0) / B=(Baseline,0,0) / C=(0,0,Baseline) に原点を置く。
        ///
        /// 誤差は概ね「距離 ÷ 基線長」に比例する。数値検証では距離4m・測距誤差1cm のとき、
        /// d=0.75m で 9cm、d=1.5m で 4.5cm、d=3.0m で 2.4cm だった。
        /// 一方で大きくしすぎると3本の入射角が揃わなくなり、
        /// 表面オフセットを定数とみなす近似が崩れる。その折衷点として 1.5m。
        /// PC側の計算と必ず同じ値にすること。
        /// </summary>
        /// <remarks>
        /// VRCDollyPivotManager.py の PROBE_BASELINE と一致必須。
        /// 三辺測量の分母なので、食い違うと距離が丸ごと圧縮/拡大される。
        /// ここを変えたときは PC 側も同じ値に直して両方をビルドし直すこと。
        /// </remarks>
        private const float Baseline = 1.5f;

        /// <summary>3原点のローカル位置。名前はパラメータ名の接尾辞にもなる。</summary>
        private static readonly (string Name, Vector3 Offset)[] ProbeAxes =
        {
            ("A", new Vector3(0f, 0f, 0f)),
            ("B", new Vector3(Baseline, 0f, 0f)),
            ("C", new Vector3(0f, 0f, Baseline)),
        };

        /// <summary>
        /// パラメータをネットワーク同期させないか。
        ///
        /// true（既定）だと Expression Parameters には登録されるが Synced が外れ、
        /// 同期ビット(256bit)を消費しない。Synced が OFF でも OSC には値が出るため、
        /// 他プレイヤーに見せる必要のないこれらのパラメータはこれでよい。
        /// </summary>
        private const bool LocalOnly = true;

        [MenuItem(MenuPath, true)]
        private static bool ValidateRun() => FindAvatar() != null;

        [MenuItem(MenuPath)]
        private static void Run()
        {
            var avatar = FindAvatar();
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("CamDrone Probe",
                    "VRCAvatarDescriptor を持つアバターを選択してから実行してください。",
                    "OK");
                return;
            }

            // FloorPointer は別配布のアセット。固定点が無ければ測距の対象が決まらない。
            var floorPointer = avatar.transform.Find(FloorPointerName);
            if (floorPointer == null)
            {
                var warning =
                    $"アバター直下に '{FloorPointerName}' が見つかりません。\n\n" +
                    "このツールは FloorPointer の固定点（Object_1〜Object_5）へ" +
                    "測距用のレイを取り付けるアドオンです。\n" +
                    "先に FloorPointer を導入し、アバター直下に配置してください。";
                Debug.LogWarning($"[CamDrone Probe] {warning.Replace("\n", " ")}", avatar);
                EditorUtility.DisplayDialog("CamDrone Probe", warning, "OK");
                return;
            }

            var slots = new List<Transform>();
            for (var i = 1; i <= SlotCount; i++)
            {
                var slot = floorPointer.Find("Object_" + i);
                if (slot == null)
                {
                    var warning =
                        $"'{FloorPointerName}/Object_{i}' が見つかりません。\n\n" +
                        $"FloorPointer に Object_1〜Object_{SlotCount} が揃っている必要があります。" +
                        "構成を確認してください。";
                    Debug.LogWarning($"[CamDrone Probe] {warning.Replace("\n", " ")}", floorPointer);
                    EditorUtility.DisplayDialog("CamDrone Probe", warning, "OK");
                    return;
                }

                slots.Add(slot);
            }

            var controller = BuildController();
            if (controller == null) return;

            Undo.SetCurrentGroupName("Setup CamDrone Player Probe");
            var undoGroup = Undo.GetCurrentGroup();

            var probeRoot = EnsureChild(avatar.transform, ProbeRootName);
            ConfigureMergeAnimator(probeRoot.gameObject, controller);
            ConfigureParameters(probeRoot.gameObject);

            var aimTarget = EnsureChild(probeRoot, AimTargetName);
            ConfigureAimTarget(aimTarget.gameObject);

            var warnings = new List<string>();
            for (var i = 0; i < SlotCount; i++)
            {
                // 1本構成だった頃の残骸を掃除する
                var legacy = slots[i].Find("PlayerProbe");
                if (legacy != null) Undo.DestroyObjectImmediate(legacy.gameObject);

                // ProbeRig は位置は固定点のまま、向きだけプレイヤーに追従させる。
                // これで3原点がプレイヤーの向いている座標系に並び、
                // 三辺測量の答えがそのまま「プレイヤーから見た x, z」になる。
                var rig = EnsureChild(slots[i], RigChildName);
                rig.localPosition = Vector3.zero;
                rig.localRotation = Quaternion.identity;
                rig.localScale = Vector3.one;
                ConfigureRotationConstraint(rig.gameObject, probeRoot, warnings);
                ConfigureWorldScale(rig.gameObject);

                foreach (var axis in ProbeAxes)
                {
                    var probe = EnsureChild(rig, "Probe" + axis.Name);
                    probe.localPosition = axis.Offset;
                    probe.localRotation = Quaternion.identity;

                    ConfigureAimConstraint(probe.gameObject, aimTarget, warnings);

                    var result = EnsureChild(probe, ResultChildName);
                    ConfigureRaycast(probe.gameObject, ParamBase(i + 1, axis.Name), result, warnings);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            EditorUtility.SetDirty(avatar);

            var message =
                $"{SlotCount} 点 × 3本 = {SlotCount * ProbeAxes.Length} 本のレイを設定しました。\n\n" +
                $"パラメータ: {ParamBase(1, "A")} 形式で _Hit / _Ratio / _Distance（全て Local Only）\n" +
                $"レイ最大距離: {MaxDistance} m（_Ratio × {MaxDistance} が実距離）\n" +
                $"基線長: {Baseline} m（PC側の計算と揃えること）\n\n" +
                "シーンを保存してからアバターをアップロードしてください。";

            if (warnings.Count > 0)
            {
                message += "\n\n[要確認]\n" + string.Join("\n", warnings);
                Debug.LogWarning("[CamDrone Probe] " + string.Join(" / ", warnings));
            }

            EditorUtility.DisplayDialog("CamDrone Probe", message, "OK");
        }

        [MenuItem(RemoveMenuPath, true)]
        private static bool ValidateRemove() => FindAvatar() != null;

        [MenuItem(RemoveMenuPath)]
        private static void Remove()
        {
            var avatar = FindAvatar();
            if (avatar == null) return;

            Undo.SetCurrentGroupName("Remove CamDrone Player Probe");
            var undoGroup = Undo.GetCurrentGroup();

            var probeRoot = avatar.transform.Find(ProbeRootName);
            if (probeRoot != null) Undo.DestroyObjectImmediate(probeRoot.gameObject);

            var floorPointer = avatar.transform.Find(FloorPointerName);
            if (floorPointer != null)
            {
                for (var i = 1; i <= SlotCount; i++)
                {
                    var slot = floorPointer.Find("Object_" + i);
                    if (slot == null) continue;

                    var rig = slot.Find(RigChildName);
                    if (rig != null) Undo.DestroyObjectImmediate(rig.gameObject);

                    // 1本構成だった頃の残骸も掃除する
                    var legacy = slot.Find("PlayerProbe");
                    if (legacy != null) Undo.DestroyObjectImmediate(legacy.gameObject);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("[CamDrone Probe] プローブを削除しました。");
        }

        // -------------------------------------------------------------------
        // シーン側の構築
        // -------------------------------------------------------------------

        /// <summary>VRCRaycast の Parameter に入れる名前。SDK が _Hit / _Ratio / _Distance を付け足す。</summary>
        private static string ParamBase(int slot, string axis) => $"{ParamPrefix}{slot}_{axis}";

        private static VRCAvatarDescriptor FindAvatar()
        {
            var go = Selection.activeGameObject;
            return go == null ? null : go.GetComponentInParent<VRCAvatarDescriptor>();
        }

        private static Transform EnsureChild(Transform parent, string name)
        {
            var existing = parent.Find(name);
            if (existing != null) return existing;

            var go = new GameObject(name);
            Undo.RegisterCreatedObjectUndo(go, "Create " + name);
            Undo.SetTransformParent(go.transform, parent, "Parent " + name);
            go.transform.localPosition = Vector3.zero;
            go.transform.localRotation = Quaternion.identity;
            go.transform.localScale = Vector3.one;
            return go.transform;
        }

        private static T EnsureComponent<T>(GameObject go) where T : Component
        {
            var c = go.GetComponent<T>();
            return c != null ? c : Undo.AddComponent<T>(go);
        }

        private static void ConfigureMergeAnimator(GameObject go, RuntimeAnimatorController controller)
        {
            var merge = EnsureComponent<ModularAvatarMergeAnimator>(go);
            Undo.RecordObject(merge, "Configure Merge Animator");
            merge.animator = controller;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.deleteAttachedAnimator = false;
            merge.pathMode = MergeAnimatorPathMode.Absolute;
            merge.matchAvatarWriteDefaults = true;
            EditorUtility.SetDirty(merge);
        }

        private static void ConfigureParameters(GameObject go)
        {
            var parameters = EnsureComponent<ModularAvatarParameters>(go);
            Undo.RecordObject(parameters, "Configure Parameters");

            // syncType は NotSynced 以外にしないと Expression Parameters に登録されず OSC に出ない。
            // localOnly（= LocalOnly 定数）で networkSynced が false になり、同期ビットは消費しない。
            var configs = new List<ParameterConfig>();
            foreach (var name in ParameterNames())
            {
                configs.Add(MakeParam(name, IsHitParam(name) ? ParameterSyncType.Bool : ParameterSyncType.Float));
            }

            parameters.parameters = configs;
            EditorUtility.SetDirty(parameters);
        }

        private static bool IsHitParam(string name) =>
            name.EndsWith(VRCRaycast.PARAM_HIT, StringComparison.Ordinal);

        /// <summary>VRCRaycast が駆動する全パラメータ名。Animator と MA Parameters で同じ一覧を使う。</summary>
        private static IEnumerable<string> ParameterNames()
        {
            for (var slot = 1; slot <= SlotCount; slot++)
            {
                foreach (var axis in ProbeAxes)
                {
                    var baseName = ParamBase(slot, axis.Name);
                    yield return baseName + VRCRaycast.PARAM_HIT;
                    yield return baseName + VRCRaycast.PARAM_RATIO;
                    yield return baseName + VRCRaycast.PARAM_DISTANCE;
                }
            }
        }

        private static ParameterConfig MakeParam(string name, ParameterSyncType type)
        {
            return new ParameterConfig
            {
                nameOrPrefix = name,
                remapTo = "",
                internalParameter = false,
                isPrefix = false,
                syncType = type,
                localOnly = LocalOnly,
                defaultValue = 0f,
                saved = false,
            };
        }

        private static void ConfigureAimTarget(GameObject go)
        {
            var proxy = EnsureComponent<ModularAvatarBoneProxy>(go);
            Undo.RecordObject(proxy, "Configure Bone Proxy");
            // 胸に追従させる。固定点は床にあり、レイは斜め上に飛ぶので、
            // 腰を狙うと近距離で脚のコライダーに先に当たる。胸なら脚から離れる。
            proxy.boneReference = HumanBodyBones.Chest;
            proxy.subPath = "";
            proxy.attachmentMode = BoneProxyAttachmentMode.AsChildAtRoot;
            EditorUtility.SetDirty(proxy);
        }

        /// <summary>
        /// ProbeRig のワールドスケールを 1 に固定する。
        ///
        /// Object_N はアバター階層の中にあるため、アバタースケールを変えると
        /// 一緒に伸縮する。すると ProbeB/ProbeC のローカル 1.5m がワールドでは
        /// 1.5m でなくなり、基線長が変わって三辺測量の答えがずれる。
        /// 一方 VRCRaycast は applyTransformScale を false にしてあるので
        /// 測距はワールド単位のまま。基線だけが動くという食い違いになる。
        ///
        /// MA World Scale Object はビルド時に VRCScaleConstraint を足し、
        /// アバター階層の外にある localScale 1 のプレハブをソースにする。
        /// これでアバターを何倍にしてもワールドスケールが 1 に保たれる。
        /// </summary>
        private static void ConfigureWorldScale(GameObject go)
        {
            EnsureComponent<ModularAvatarWorldScaleObject>(go);
        }

        /// <summary>
        /// ProbeRig の向きをプレイヤーの向きに合わせる（Y軸のみ）。位置は固定点のまま。
        /// ソースにはアバタールート直下の probeRoot を使う。probeRoot の
        /// ワールド回転はアバタールートの回転そのものなので、別途 BoneProxy は不要。
        /// </summary>
        private static void ConfigureRotationConstraint(GameObject go, Transform yawSource, List<string> warnings)
        {
            var rotation = EnsureComponent<VRCRotationConstraint>(go);
            using (var so = new SerializedObject(rotation))
            {
                SetBool(so, true, warnings, "IsActive", "IsContraintActive", "m_IsContraintActive");
                SetBool(so, true, null, "Locked", "IsLocked", "m_IsLocked");
                SetFloat(so, 1f, null, "GlobalWeight", "Weight", "m_Weight");
                SetBool(so, false, null, "AffectsRotationX", "AffectRotationX", "m_AffectRotationX");
                SetBool(so, true, null, "AffectsRotationY", "AffectRotationY", "m_AffectRotationY");
                SetBool(so, false, null, "AffectsRotationZ", "AffectRotationZ", "m_AffectRotationZ");
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            SetSingleSource(rotation, yawSource, "VRCRotationConstraint", warnings);
            EditorUtility.SetDirty(rotation);
        }

        private static void ConfigureAimConstraint(GameObject go, Transform aimTarget, List<string> warnings)
        {
            var aim = EnsureComponent<VRCAimConstraint>(go);
            using (var so = new SerializedObject(aim))
            {
                // VRC のコンストレイントは Unity 標準の m_AimVector 系ではなく
                // AimAxis / UpAxis / GlobalWeight / IsActive / Locked という名前を使う。
                SetVector3(so, new Vector3(0f, 0f, 1f), warnings, "AimAxis", "AimVector", "m_AimVector");
                SetVector3(so, new Vector3(0f, 1f, 0f), null, "UpAxis", "m_UpVector");
                SetBool(so, true, warnings, "IsActive", "IsContraintActive", "m_IsContraintActive");
                SetBool(so, true, null, "Locked", "IsLocked", "m_IsLocked");
                SetFloat(so, 1f, null, "GlobalWeight", "Weight", "m_Weight");
                SetBool(so, true, null, "AffectsRotationX", "AffectRotationX", "m_AffectRotationX");
                SetBool(so, true, null, "AffectsRotationY", "AffectRotationY", "m_AffectRotationY");
                SetBool(so, true, null, "AffectsRotationZ", "AffectRotationZ", "m_AffectRotationZ");
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            SetSingleSource(aim, aimTarget, "VRCAimConstraint", warnings);
            EditorUtility.SetDirty(aim);
        }

        /// <summary>
        /// 自分のプレイヤーコライダーだけに当たるようレイヤーを絞る。
        /// レイヤーが見つからない場合は HitPlayers（他人にも当たる）に落とす。
        /// </summary>
        private static void SetCollisionLayers(SerializedObject so, List<string> warnings)
        {
            // 列挙体はインデックスを決め打ちせず、名前で引く。
            var collisionMode = FindProp(so, "collisionMode");
            var layer = LayerMask.NameToLayer(SelfCollisionLayer);

            if (layer < 0)
            {
                warnings.Add($"レイヤー '{SelfCollisionLayer}' が見つかりません。Collision Mode は Hit Players のままです。");
                SetEnumByName(collisionMode, "HitPlayers");
                return;
            }

            if (!SetEnumByName(collisionMode, "HitCustomLayers"))
            {
                warnings.Add("VRCRaycast の Collision Mode を 'Hit Custom Layers' に手で設定してください。");
                if (collisionMode != null)
                {
                    Debug.LogWarning("[CamDrone Probe] collisionMode の候補: "
                                     + string.Join(", ", collisionMode.enumNames));
                }

                return;
            }

            var layers = FindProp(so, "customCollisionLayers");
            if (layers == null)
            {
                warnings.Add($"VRCRaycast の Custom Collision Layers に '{SelfCollisionLayer}' だけを手で設定してください。");
                return;
            }

            layers.intValue = 1 << layer;
        }

        // -------------------------------------------------------------------
        // Sources の設定
        //
        // VRC のコンストレイントの Sources は VRCConstraintSourceKeyableList 型で、
        // 配列ではなく source0〜source15 という16個の平坦なフィールドを持つ
        // （アニメーション可能にするため配列を避けている）。
        // シリアライズ名を当てにいくと外すので、まず公開APIをリフレクションで叩き、
        // 駄目なら SerializedProperty で平坦フィールドを探しにいく。
        // -------------------------------------------------------------------

        private const BindingFlags MemberFlags =
            BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

        private static void SetSingleSource(Component constraint, Transform source, string label,
            List<string> warnings)
        {
            if (TrySetSourceByReflection(constraint, source, out var reflectionDetail)) return;

            using (var so = new SerializedObject(constraint))
            {
                if (TrySetSourceBySerializedProperty(so, source))
                {
                    so.ApplyModifiedPropertiesWithoutUndo();
                    return;
                }

                warnings.Add($"{label} の Sources に {source.name} を手で設定してください。");
                Debug.LogWarning($"[CamDrone Probe] {label} の Sources 自動設定に失敗: {reflectionDetail}");
                DumpSerializedTree(so, label);
            }
        }

        private static bool TrySetSourceByReflection(Component constraint, Transform source, out string detail)
        {
            var sourcesProperty = constraint.GetType().GetProperty("Sources", MemberFlags);
            var list = sourcesProperty?.GetValue(constraint);
            if (list == null)
            {
                detail = "Sources プロパティを取得できない";
                return false;
            }

            var listType = list.GetType();
            Type elementType = null;
            foreach (var iface in listType.GetInterfaces())
            {
                if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IList<>)) continue;
                elementType = iface.GetGenericArguments()[0];
                break;
            }

            if (elementType == null)
            {
                detail = $"{listType.Name} が IList<> を実装していない";
                return false;
            }

            // Modular Avatar にも Activator という型があるので完全修飾する
            // 既に目的の Transform が入っていれば触らない。
            // 手で設定した内容を再実行で壊さないため。
            if (ContainsSource(list, elementType, source))
            {
                detail = "";
                return true;
            }

            var element = System.Activator.CreateInstance(elementType);
            if (!TryAssignMember(elementType, element, typeof(Transform), source))
            {
                detail = $"{elementType.Name} に Transform のメンバーが無い";
                return false;
            }

            TryAssignMember(elementType, element, typeof(float), 1f);

            var add = listType.GetMethod("Add", MemberFlags, null, new[] { elementType }, null);
            if (add == null)
            {
                detail = $"{listType.Name} に Add( {elementType.Name} ) が無い";
                return false;
            }

            // Clear を先に呼ぶと、Add が失敗したときに既存の設定だけ消える。
            // 必ず Add を先に通し、成功してから余りを削る。
            try
            {
                add.Invoke(list, new object[] { element });
            }
            catch (Exception exception)
            {
                detail = exception.GetBaseException().Message;
                return false;
            }

            TrimToLastSource(list, listType);

            // 値型なら GetValue が返したのは複製なので、書き戻さないと反映されない。
            if (listType.IsValueType)
            {
                if (sourcesProperty.CanWrite)
                {
                    sourcesProperty.SetValue(constraint, list);
                }
                else
                {
                    var backing = FindFieldOfType(constraint.GetType(), listType);
                    if (backing == null)
                    {
                        detail = $"{listType.Name} は値型だが書き戻し先が見つからない";
                        return false;
                    }

                    backing.SetValue(constraint, list);
                }
            }

            detail = "";
            return true;
        }

        /// <summary>Sources に既に目的の Transform が入っているか。</summary>
        private static bool ContainsSource(object list, Type elementType, Transform source)
        {
            if (!(list is System.Collections.IEnumerable enumerable)) return false;

            foreach (var entry in enumerable)
            {
                if (entry == null) continue;
                if (ReadTransformMember(elementType, entry) == source) return true;
            }

            return false;
        }

        private static Transform ReadTransformMember(Type type, object instance)
        {
            foreach (var property in type.GetProperties(MemberFlags))
            {
                if (!property.CanRead || !typeof(Transform).IsAssignableFrom(property.PropertyType)) continue;
                return property.GetValue(instance) as Transform;
            }

            foreach (var field in type.GetFields(MemberFlags))
            {
                if (!typeof(Transform).IsAssignableFrom(field.FieldType)) continue;
                return field.GetValue(instance) as Transform;
            }

            return null;
        }

        /// <summary>最後に追加した1件だけを残す。RemoveAt が無ければ何もしない。</summary>
        private static void TrimToLastSource(object list, Type listType)
        {
            var countProperty = listType.GetProperty("Count", MemberFlags);
            var removeAt = listType.GetMethod("RemoveAt", MemberFlags, null, new[] { typeof(int) }, null);
            if (countProperty == null || removeAt == null) return;

            try
            {
                while (countProperty.GetValue(list) is int count && count > 1)
                {
                    removeAt.Invoke(list, new object[] { 0 });
                }
            }
            catch (Exception exception)
            {
                Debug.LogWarning("[CamDrone Probe] Sources の余分な要素を削除できませんでした: "
                                 + exception.GetBaseException().Message);
            }
        }

        private static FieldInfo FindFieldOfType(Type owner, Type fieldType)
        {
            for (var type = owner; type != null; type = type.BaseType)
            {
                foreach (var field in type.GetFields(MemberFlags))
                {
                    if (field.FieldType == fieldType) return field;
                }
            }

            return null;
        }

        private static bool TryAssignMember(Type type, object target, Type valueType, object value)
        {
            foreach (var property in type.GetProperties(MemberFlags))
            {
                if (!property.CanWrite || !property.PropertyType.IsAssignableFrom(valueType)) continue;
                property.SetValue(target, value);
                return true;
            }

            foreach (var field in type.GetFields(MemberFlags))
            {
                if (!field.FieldType.IsAssignableFrom(valueType)) continue;
                field.SetValue(target, value);
                return true;
            }

            return false;
        }

        private static bool TrySetSourceBySerializedProperty(SerializedObject so, Transform source)
        {
            var root = FindProp(so, "Sources", "sources", "m_Sources");
            if (root == null) return false;

            var array = FindArrayWithin(root);
            if (array != null)
            {
                array.arraySize = 1;
                return AssignSourceSlot(array.GetArrayElementAtIndex(0), source);
            }

            // source0 〜 source15 の平坦な構成
            var slot = root.FindPropertyRelative("source0")
                       ?? root.FindPropertyRelative("Source0")
                       ?? root.FindPropertyRelative("_source0");
            if (slot == null || !AssignSourceSlot(slot, source)) return false;

            SetSourceCount(root, 1);
            return true;
        }

        private static bool AssignSourceSlot(SerializedProperty slot, Transform source)
        {
            var transform = FindRelative(slot, SerializedPropertyType.ObjectReference,
                "SourceTransform", "sourceTransform", "transform");
            if (transform == null) return false;

            transform.objectReferenceValue = source;

            var weight = FindRelative(slot, SerializedPropertyType.Float, "Weight", "weight");
            if (weight != null) weight.floatValue = 1f;
            return true;
        }

        private static void SetSourceCount(SerializedProperty root, int count)
        {
            var end = root.GetEndProperty();
            var iterator = root.Copy();
            while (iterator.NextVisible(true) && !SerializedProperty.EqualContents(iterator, end))
            {
                if (iterator.propertyType != SerializedPropertyType.Integer) continue;
                if (iterator.name.IndexOf("count", StringComparison.OrdinalIgnoreCase) < 0) continue;
                iterator.intValue = count;
                return;
            }
        }

        private static void DumpSerializedTree(SerializedObject so, string label)
        {
            var iterator = so.GetIterator();
            var names = new List<string>();
            while (iterator.NextVisible(true) && names.Count < 200)
            {
                names.Add($"{iterator.propertyPath} ({iterator.propertyType})");
            }

            Debug.LogWarning($"[CamDrone Probe] {label} のプロパティ一覧:\n" + string.Join("\n", names));
        }

        private static void ConfigureRaycast(GameObject go, string parameterName, Transform result, List<string> warnings)
        {
            var raycast = EnsureComponent<VRCRaycast>(go);
            using (var so = new SerializedObject(raycast))
            {
                SetVector3(so, new Vector3(0f, 0f, 1f), warnings, "raycastDirection");
                SetFloat(so, MaxDistance, warnings, "distance");
                // 距離の動的切り替え（25m/50m）は applyTransformScale を true にして
                // probe の Z スケールをアニメーションする方式に決定済み。
                // ただし本実装は後回しなので、今は false のままにしておく。
                // true にすると実効距離が親のスケール連鎖に依存するようになり、
                // アバターのスケールが 1 でない場合に現在の測定値が変わってしまうため。
                SetBool(so, false, null, "applyTransformScale");
                SetBool(so, false, null, "applyRotation");
                SetString(so, parameterName, warnings, "parameter");

                // Result Transform は本来ヒット点にオブジェクトを置くための機能で、
                // パラメータ出力とは別物のはず。ただし null のときに
                // コンポーネントごと早期リターンしない保証が取れなかったため、
                // 検証が空振りしないよう必ず埋めておく。
                // Unity 上でヒット点が可視化できるという副次的な利点もある。
                var resultTransform = FindProp(so, "resultTransform");
                if (resultTransform != null) resultTransform.objectReferenceValue = result;
                else warnings.Add("VRCRaycast の Result Transform に " + ResultChildName + " を手で設定してください。");

                // 外れたときに Result を始点へ戻す。既定のまま終点へ飛ばすと
                // MaxDistance の分だけ遠方へ移動し、可視物を付けた場合に
                // アバターのバウンディングボックスがそこまで膨らむ。
                SetEnumByName(FindProp(so, "behaviorOnMiss"), "SnapToStart");

                SetCollisionLayers(so, warnings);
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            EditorUtility.SetDirty(raycast);
        }

        // -------------------------------------------------------------------
        // Animator Controller の生成
        // -------------------------------------------------------------------

        /// <summary>
        /// パラメータを宣言するだけの Controller を作る。
        ///
        /// 「宣言するだけ」だと Avatar Optimizer の Trace and Optimize が
        /// 「どのアニメータもこのパラメータを使っていない」と判断して
        /// VRCRaycast ごと削除してしまう可能性があるため、
        /// 絶対に成立しない条件の遷移を 1 本置いて全パラメータを参照させておく。
        /// </summary>
        private static AnimatorController BuildController()
        {
            var dir = Path.GetDirectoryName(ControllerPath);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
                AssetDatabase.Refresh();
            }

            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(ControllerPath);
            if (existing != null) AssetDatabase.DeleteAsset(ControllerPath);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(ControllerPath);
            if (controller == null)
            {
                EditorUtility.DisplayDialog("CamDrone Probe",
                    "Animator Controller を作成できませんでした: " + ControllerPath, "OK");
                return null;
            }

            var names = new List<string>(ParameterNames());
            foreach (var name in names)
            {
                controller.AddParameter(name,
                    IsHitParam(name)
                        ? AnimatorControllerParameterType.Bool
                        : AnimatorControllerParameterType.Float);
            }

            var layer = controller.layers[0];
            layer.name = "CamDroneProbe";
            controller.layers = new[] { layer };

            var machine = layer.stateMachine;
            var idle = machine.AddState("Idle");
            idle.writeDefaultValues = true;
            var never = machine.AddState("Never");
            never.writeDefaultValues = true;
            machine.defaultState = idle;

            // 全条件の AND。_Ratio / _Distance が同時に 999 を超えることはないので発火しない。
            var transition = idle.AddTransition(never);
            transition.hasExitTime = false;
            transition.duration = 0f;
            foreach (var name in names)
            {
                if (IsHitParam(name))
                    transition.AddCondition(AnimatorConditionMode.If, 0f, name);
                else
                    transition.AddCondition(AnimatorConditionMode.Greater, 999f, name);
            }

            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssets();
            return controller;
        }

        // -------------------------------------------------------------------
        // SerializedProperty ヘルパー
        //
        // VRC の各コンポーネントのシリアライズ名は SDK 内部の実装依存なので、
        // 候補名 → 部分一致の順で探し、見つからなければ警告して手作業に回す。
        // -------------------------------------------------------------------

        private static SerializedProperty FindProp(SerializedObject so, params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var prop = so.FindProperty(candidate);
                if (prop != null) return prop;
            }

            var iterator = so.GetIterator();
            var enterChildren = true;
            while (iterator.NextVisible(enterChildren))
            {
                enterChildren = false;
                foreach (var candidate in candidates)
                {
                    var bare = candidate.TrimStart('m', '_');
                    if (iterator.name.IndexOf(bare, StringComparison.OrdinalIgnoreCase) >= 0)
                        return so.FindProperty(iterator.propertyPath);
                }
            }

            return null;
        }

        private static bool IsRealArray(SerializedProperty prop)
        {
            return prop.isArray && prop.propertyType != SerializedPropertyType.String;
        }

        /// <summary>
        /// 配列そのもの、または配列を内側に持つ構造体を受け取り、実体の配列プロパティを返す。
        /// </summary>
        private static SerializedProperty FindArrayWithin(SerializedProperty root)
        {
            if (root == null) return null;
            if (IsRealArray(root)) return root;

            var end = root.GetEndProperty();
            var iterator = root.Copy();
            while (iterator.NextVisible(true) && !SerializedProperty.EqualContents(iterator, end))
            {
                if (IsRealArray(iterator)) return iterator.Copy();
            }

            return null;
        }

        /// <summary>
        /// 配列要素の中から、指定した型のプロパティを名前候補で探す。
        /// 名前が合わなくても、その型のプロパティが見つかっていればそれを返す。
        /// </summary>
        private static SerializedProperty FindRelative(SerializedProperty element, SerializedPropertyType type,
            params string[] candidates)
        {
            foreach (var candidate in candidates)
            {
                var prop = element.FindPropertyRelative(candidate);
                if (prop != null && prop.propertyType == type) return prop;
            }

            SerializedProperty firstOfType = null;
            var end = element.GetEndProperty();
            var iterator = element.Copy();
            while (iterator.NextVisible(true) && !SerializedProperty.EqualContents(iterator, end))
            {
                if (iterator.propertyType != type) continue;
                if (firstOfType == null) firstOfType = iterator.Copy();

                foreach (var candidate in candidates)
                {
                    if (iterator.name.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0)
                        return iterator.Copy();
                }
            }

            return firstOfType;
        }

        private static void Missing(List<string> warnings, string label)
        {
            warnings?.Add(label + " を自動設定できませんでした。インスペクタで確認してください。");
        }

        /// <summary>
        /// 名前が見つかっても型が違えば書き込まない。誤ったプロパティを潰すより
        /// 警告を出して手作業に回した方が安全なため。
        /// </summary>
        private static SerializedProperty FindTyped(SerializedObject so, SerializedPropertyType type,
            List<string> warnings, string[] names)
        {
            var prop = FindProp(so, names);
            if (prop == null || prop.propertyType != type)
            {
                Missing(warnings, names[0]);
                return null;
            }

            return prop;
        }

        private static void SetVector3(SerializedObject so, Vector3 value, List<string> warnings, params string[] names)
        {
            var prop = FindTyped(so, SerializedPropertyType.Vector3, warnings, names);
            if (prop != null) prop.vector3Value = value;
        }

        private static void SetFloat(SerializedObject so, float value, List<string> warnings, params string[] names)
        {
            var prop = FindTyped(so, SerializedPropertyType.Float, warnings, names);
            if (prop != null) prop.floatValue = value;
        }

        private static void SetBool(SerializedObject so, bool value, List<string> warnings, params string[] names)
        {
            var prop = FindTyped(so, SerializedPropertyType.Boolean, warnings, names);
            if (prop != null) prop.boolValue = value;
        }

        private static void SetString(SerializedObject so, string value, List<string> warnings, params string[] names)
        {
            var prop = FindTyped(so, SerializedPropertyType.String, warnings, names);
            if (prop != null) prop.stringValue = value;
        }

        private static bool SetEnumByName(SerializedProperty prop, params string[] names)
        {
            if (prop == null || prop.propertyType != SerializedPropertyType.Enum) return false;

            var options = prop.enumNames;
            foreach (var name in names)
            {
                for (var i = 0; i < options.Length; i++)
                {
                    if (!string.Equals(options[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                    prop.enumValueIndex = i;
                    return true;
                }
            }

            return false;
        }

    }
}
