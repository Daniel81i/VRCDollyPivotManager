using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using nadena.dev.modular_avatar.core;
using VRC.SDK3.Avatars.Components;
using VRC.SDK3.Avatars.ScriptableObjects;
using VRC.SDK3.Dynamics.Constraint.Components;

namespace Daniel81i.VRChatCamDolly.EditorTools
{
    /// <summary>
    /// FloorPointer の固定点（Object_1〜Object_5）それぞれに、旋回円を調整するための
    /// ガイドとメニューを作る。
    ///
    /// 各固定点の下に:
    ///   Pillar        仮想床からワールド垂直に伸びる細いシリンダー（メッシュ）
    ///   Center        高さ調整で Y に上下する入れ物
    ///     Cube        旋回中心の実体（既存のものがあればそれを使う）
    ///     Marker      旋回中心を指す印。テクスチャを貼った 1 粒
    ///   RingCenter    円の高さ。中心点とは独立して動かせる
    ///     TiltRing    旋回円のパーティクル。一様スケールが半径
    ///     LowPoint    傾けたときの最下点を指す印。テクスチャを貼った 1 粒
    ///
    /// VRChat のパーティクル枠（Poor で 16 システム / 2500 粒）に 5 スロット分を
    /// 収める必要があるため、1 スロットあたり 3 システムに抑えている。
    /// 柱はメッシュ、印は 1 粒ずつ、水平の参照円と床の印は持たない。
    ///
    /// メニューは根で Pivot と Camera に分かれる。
    ///   Pivot  → Pivot 1〜5 → 高さ・半径・傾き・表示切替・Confirm
    ///   Camera → Lens（Zoom / FocalDistance / Aperture）と Motion（Duration / Speed）
    /// カメラ設定はスロットに属さない。同時に1本しかパスを作らないため。
    ///
    /// 生成物はすべてシーン上のアバターインスタンス配下のオーバーライドとして作られる。
    /// アバターや FloorPointer の prefab アセット本体には書き込まない。
    /// </summary>
    internal static class CamDroneOrbitGuideSetup
    {
        private const string MenuPath = "Tools/CamDrone/Setup Orbit Guide";
        private const string RemoveMenuPath = "Tools/CamDrone/Remove Orbit Guide";

        private const string AssetDir = "Assets/Daniel81i/VRChatCamDolly";
        private const string FloorPointerName = "FloorPointer";
        private const string MenuRootName = "CamDrone Orbit Menu";
        private const string GuideRootName = "OrbitGuide";
        private const string CenterName = "Center";
        private const string CubeName = "Cube";
        private const string PillarName = "Pillar";
        private const string MarkerName = "Marker";
        private const string FloorMarkerName = "FloorMarker";
        private const string RingCenterName = "RingCenter";
        private const string RingName = "Ring";
        private const string YawFollowName = "YawFollow";
        private const string TiltPivotName = "TiltPivot";
        private const string TiltRingName = "TiltRing";

        private const string TiltAzimuthName = "TiltAzimuth";
        private const string LowPointName = "LowPoint";

        private static string TiltAzimuthPath => RingCenterName + "/" + YawFollowName + "/" + TiltAzimuthName;
        private static string TiltPivotPath => TiltAzimuthPath + "/" + TiltPivotName;
        private static string TiltRingPath => TiltPivotPath + "/" + TiltRingName;
        private static string LowPointPath => TiltPivotPath + "/" + LowPointName;

        // -------------------------------------------------------------------
        // PC 側と一致必須の定数
        //
        // パペットは 0〜1 の正規化値しか送らないので、実寸へ戻すのは PC 側。
        // 以下の範囲は VRCDollyPivotManager.py にも同じ値が置いてあり、
        // 片方だけ変えると届く % は同じでも別の実寸になり、生成結果が静かにずれる。
        //
        // ここを変えたときは VRCDollyPivotManager.py の対応する定数も同じ値に
        // 直して、アバターと exe の両方をビルドし直すこと。
        // 対応表は camdrone-dolly-tray の README「アバターと一致必須の値」にある。
        // -------------------------------------------------------------------

        private const int SlotCount = 5;                // SLOT_COUNT

        // 高さ: 仮想床からの相対。既定 1.2 m
        private const float HeightMin = -1.5f;          // HEIGHT_MIN
        private const float HeightMax = 4.0f;           // HEIGHT_MAX
        private const float HeightDefault = 1.2f;       // DEFAULT_MENU["Height"]

        // 旋回円の半径。既定 2 m
        private const float RadiusMin = 1.0f;           // RADIUS_MIN
        private const float RadiusMax = 25.0f;          // RADIUS_MAX
        private const float RadiusDefault = 2.0f;       // DEFAULT_MENU["Radius"]

        /// <summary>棒パーティクルは高さの可動範囲全体をカバーする長さにする。</summary>
        private const float PillarThickness = 0.02f;

        // 旋回円のパーティクル。
        // 寿命 = 半径を動かしたときに残る尾の長さ。短いほど追従が良い。
        // 常時表示数 = 円周に撒く粒の数。半径によらず一定なので、
        // 半径が大きいほど破線が粗くなる（半径 7m で約 15cm 間隔）。
        private const float RingDotSize = 0.02f;
        private const float RingLifetime = 0.3f;
        private const float RingLiveCount = 300f;
        private const int RingMaxParticles = 320;

        // 配色。中心軸・旋回円・最下点・仮想床を色でも見分けられるようにする。
        //
        // 中心軸と旋回円が同じ色だと、どちらの円を見ているのか分からなくなる。
        // シアンとオレンジは補色に近く、VR の雑多な背景でも取り違えにくい。
        // 最下点は旋回円の上にある1点なので、円より目立つ色を当てる。
        private static readonly Color AxisColor = new Color(0.30f, 0.90f, 1.00f);
        private static readonly Color RingColor = new Color(1.00f, 0.55f, 0.10f);
        private static readonly Color LowPointColor = new Color(1.00f, 0.30f, 0.85f);
        private static readonly Color FloorColor = new Color(0.60f, 1.00f, 0.30f);

        // 位置を指す印に貼るテクスチャ。用途ごとに絵と向きを変える。
        //
        // 床に立てる2つ（中心軸・最下点）は VerticalBillboard にして、
        // 立ったまま Y 軸まわりに回りユーザーの方を向く。
        // 仮想床の印だけ HorizontalBillboard で地面と平行に寝かせる。
        private const string CenterTexturePath = AssetDir + "/image/marker_crosshair.png";
        private const string LowPointTexturePath = AssetDir + "/image/marker_diamond.png";
        private const string FloorTexturePath = AssetDir + "/image/marker_ring.png";

        // 目印は3種類あるので、粒の大きさで見分けられるようにする。
        // 最下点が一番目立つべきなので最大にしている。
        private const float CenterMarkerSize = 0.14f;
        private const float LowPointSize = 0.22f;
        private const float FloorMarkerSize = 0.30f;

        /// <summary>
        /// 1周あたりのポイント数の選択肢。Int パラメータにそのまま点数が入るので、
        /// PC 側は変換なしでそのまま使える。
        /// </summary>
        private static readonly int[] PointChoices = { 3, 4, 6, 8, 12 };

        private const int PointsDefault = 6;

        /// <summary>
        /// 揺らぎの選択肢(%)。半径に対する割合で前後左右に与える。
        /// アニメーションには使わず、PC 側の生成にだけ効く。
        /// </summary>
        private static readonly int[] RandomChoices = { 0, 10, 20 };

        private const int RandomDefault = 10;

        /// <summary>周回の向き。true = 上から見て時計回り（右回り）。</summary>
        private const bool ClockwiseDefault = true;

        // 旋回円の前後方向の傾き。0 度が水平
        private const float TiltMinDeg = -30f;          // TILT_MIN
        private const float TiltMaxDeg = 30f;           // TILT_MAX
        private const float TiltDefaultDeg = 0f;        // DEFAULT_MENU["Tilt"]

        // 傾ける向き（最下点の方位）。
        //
        // YawFollow はアバタールートの向きに追従するので、局所 +Z は
        // 「プレイヤーが向いている方向＝マーカーより奥」になる。
        //
        // パラメータ 0〜1 を一周ではなく「右→奥→左」の半周に割り当てる。
        // 手前側の半周は Tilt の符号を反転すれば出せるので、一周させる必要がない。
        // 割り当てを半分にしたぶんパペットの分解能が倍になる。
        //
        //   0%   =  +90 度 = 右
        //   50%  =    0 度 = 奥
        //   100% =  -90 度 = 左
        //
        // 増える向きは VRChat のラジアルパペットのダイヤルと同じ向きに合わせてある。
        // 逆にするとパペットを右へ倒したのに最下点が左へ行く（2026-08-09 に実測）。
        //
        // ここを変えるときは VRCDollyPivotManager 側の LOW_POINT_START_DEG /
        // LOW_POINT_SWEEP_DEG も必ず同じ値にすること。片方だけ直すと
        // ガイドの目印と生成される軌道がずれる。
        private const float TiltDirMinDeg = 90f;        // LOW_POINT_START_DEG
        private const float TiltDirMaxDeg = -90f;       // + LOW_POINT_SWEEP_DEG
        private const float TiltDirDefaultDeg = 0f;     // DEFAULT_MENU["TiltDir"]

        // 生成する JSON へそのまま書くカメラ設定。全スロット共通で1組しか持たない。
        // 本仕様では一度に1本しかパスを作れないため、マーカーごとに分ける必要がない。
        //
        // アバター側では何も動かさず、PC 側の書き出しにだけ効く。
        private const float ZoomMin = 20f;              // ZOOM_MIN
        private const float ZoomMax = 150f;             // ZOOM_MAX
        private const float ZoomDefault = 45f;          // DEFAULT_CAMERA["Zoom"]

        private const float DurationMin = 0.1f;         // DURATION_MIN
        private const float DurationMax = 60f;          // DURATION_MAX
        private const float DurationDefault = 2f;       // DEFAULT_CAMERA["Duration"]

        private const float SpeedMin = 0.1f;            // SPEED_MIN
        private const float SpeedMax = 15f;             // SPEED_MAX
        private const float SpeedDefault = 3f;          // DEFAULT_CAMERA["Speed"]

        private const float FocalDistanceMin = 0f;      // FOCAL_DISTANCE_MIN
        private const float FocalDistanceMax = 10f;     // FOCAL_DISTANCE_MAX
        private const float FocalDistanceDefault = 1.5f;  // DEFAULT_CAMERA["FocalDistance"]

        private const float ApertureMin = 1.4f;         // APERTURE_MIN
        private const float ApertureMax = 32f;          // APERTURE_MAX
        private const float ApertureDefault = 15f;      // DEFAULT_CAMERA["Aperture"]

        [MenuItem(MenuPath, true)]
        private static bool ValidateRun() => FindAvatar() != null;

        [MenuItem(MenuPath)]
        private static void Run()
        {
            var avatar = FindAvatar();
            if (avatar == null)
            {
                EditorUtility.DisplayDialog("CamDrone Orbit",
                    "VRCAvatarDescriptor を持つアバターを選択してから実行してください。", "OK");
                return;
            }

            // FloorPointer は別配布のアセット。これが無いと固定点が存在しないので
            // ガイドの置き場所そのものが決まらない。導入を促して中止する。
            var floorPointer = avatar.transform.Find(FloorPointerName);
            if (floorPointer == null)
            {
                var warning =
                    $"アバター直下に '{FloorPointerName}' が見つかりません。\n\n" +
                    "このツールは FloorPointer の固定点（Object_1〜Object_5）に" +
                    "ガイドを取り付けるアドオンです。\n" +
                    "先に FloorPointer を導入し、アバター直下に配置してください。";
                Debug.LogWarning($"[CamDrone Orbit] {warning.Replace("\n", " ")}", avatar);
                EditorUtility.DisplayDialog("CamDrone Orbit", warning, "OK");
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
                    Debug.LogWarning($"[CamDrone Orbit] {warning.Replace("\n", " ")}", floorPointer);
                    EditorUtility.DisplayDialog("CamDrone Orbit", warning, "OK");
                    return;
                }

                slots.Add(slot);
            }

            EnsureDirectory(AssetDir + "/Animator");
            EnsureDirectory(AssetDir + "/Animation");
            EnsureDirectory(AssetDir + "/Expression");
            EnsureDirectory(AssetDir + "/Materials");

            var pillarMaterial = BuildGuideMaterial("CamDrone_OrbitGuide", AxisColor);
            var ringMaterial = BuildGuideMaterial("CamDrone_OrbitRing", RingColor);
            // 印は用途ごとに絵が違うので、マテリアルも分ける
            var centerMaterial = BuildMarkMaterial(
                CenterTexturePath, "CamDrone_MarkCenter", AxisColor);
            var lowPointMaterial = BuildMarkMaterial(
                LowPointTexturePath, "CamDrone_MarkLow", LowPointColor);
            var floorMaterial = BuildMarkMaterial(
                FloorTexturePath, "CamDrone_MarkFloor", FloorColor);
            var clips = BuildClips();

            Undo.SetCurrentGroupName("Setup CamDrone Orbit Guide");
            var undoGroup = Undo.GetCurrentGroup();

            var notes = new List<string>();
            var subMenus = new VRCExpressionsMenu[SlotCount];

            var menuRoot = EnsureChild(avatar.transform, MenuRootName);

            // 傾きの軸をプレイヤーの向きに合わせるためのソースにはアバタールートを使う。
            // MA のコンポーネントしか持たないオブジェクトはビルド時に中身が取り除かれ、
            // 空の GameObject として Avatar Optimizer の削除対象になり得るため、
            // 5本のコンストレイントの拠り所にはしない。
            var yawSource = avatar.transform;

            for (var i = 0; i < SlotCount; i++)
            {
                var slotNumber = i + 1;
                var guide = BuildGuideHierarchy(slots[i], pillarMaterial, ringMaterial,
                    centerMaterial, lowPointMaterial, floorMaterial, yawSource, notes);
                var controller = BuildController(slotNumber, clips);

                ConfigureMergeAnimator(guide.gameObject, controller);
                ConfigureParameters(guide.gameObject, slotNumber);

                subMenus[i] = BuildSlotMenu(slotNumber);
            }

            // カメラ設定はスロットに属さないので、メニューの根に1組だけ置く
            ConfigureMergeAnimator(menuRoot.gameObject, BuildCameraController());
            ConfigureCameraParameters(menuRoot.gameObject);

            var rootMenu = BuildRootMenu(subMenus);
            ConfigureMenuInstaller(menuRoot.gameObject, rootMenu);

            Undo.CollapseUndoOperations(undoGroup);
            AssetDatabase.SaveAssets();
            EditorUtility.SetDirty(avatar);

            var message =
                $"{SlotCount} 点に旋回円ガイドを設定しました。\n\n" +
                $"高さ（中心・円とも）: {HeightMin} 〜 {HeightMax} m（既定 {HeightDefault} m）\n" +
                $"半径: {RadiusMin} 〜 {RadiusMax} m（既定 {RadiusDefault} m）\n\n" +
                $"傾き: {TiltMinDeg} 〜 {TiltMaxDeg} 度（既定 {TiltDefaultDeg} 度）\n" +
                "傾きの向き: 0 〜 360 度（既定 0 度＝プレイヤーから見て手前）\n" +
                $"1周あたりの点数: {string.Join(" / ", PointChoices)}（既定 {PointsDefault}）\n" +
                $"揺らぎ: {string.Join("% / ", RandomChoices)}%（既定 {RandomDefault}%）\n" +
                $"周回の向き: 既定 {(ClockwiseDefault ? "右回り" : "左回り")}\n\n" +
                "メニュー: CamDrone Orbit > Object 1〜5 >\n" +
                "  Center Height / Ring Height / Ring -> Center / Radius /\n" +
                "  Tilt(Angle, Low Point) / Path(Points, 右回り, ランダム) /\n" +
                "  Guide / Confirm\n\n" +
                "ガイドは IsLocal で自分にだけ見えるようにしています。";

            if (notes.Count > 0) message += "\n\n[メモ]\n" + string.Join("\n", notes);

            EditorUtility.DisplayDialog("CamDrone Orbit", message, "OK");
        }

        [MenuItem(RemoveMenuPath, true)]
        private static bool ValidateRemove() => FindAvatar() != null;

        [MenuItem(RemoveMenuPath)]
        private static void Remove()
        {
            var avatar = FindAvatar();
            if (avatar == null) return;

            Undo.SetCurrentGroupName("Remove CamDrone Orbit Guide");
            var undoGroup = Undo.GetCurrentGroup();

            var menuRoot = avatar.transform.Find(MenuRootName);
            if (menuRoot != null) Undo.DestroyObjectImmediate(menuRoot.gameObject);

            var floorPointer = avatar.transform.Find(FloorPointerName);
            if (floorPointer != null)
            {
                for (var i = 1; i <= SlotCount; i++)
                {
                    var slot = floorPointer.Find("Object_" + i);
                    var guide = slot != null ? slot.Find(GuideRootName) : null;
                    if (guide == null) continue;

                    // 既存の Cube を取り込んでいた場合は元の位置へ戻してから消す
                    var cube = guide.Find(CenterName + "/" + CubeName);
                    if (cube != null) Undo.SetTransformParent(cube, slot, "Restore Cube");

                    Undo.DestroyObjectImmediate(guide.gameObject);
                }
            }

            Undo.CollapseUndoOperations(undoGroup);
            Debug.Log("[CamDrone Orbit] ガイドを削除しました。");
        }

        // -------------------------------------------------------------------
        // ヒエラルキー
        // -------------------------------------------------------------------

        private static Transform BuildGuideHierarchy(Transform slot,
            Material pillarMaterial, Material ringMaterial, Material centerMaterial,
            Material lowPointMaterial, Material floorMaterial,
            Transform yawSource, List<string> notes)
        {
            var guide = EnsureChild(slot, GuideRootName);
            guide.localPosition = Vector3.zero;
            guide.localRotation = Quaternion.identity;
            guide.localScale = Vector3.one;

            // Object_N はアバター階層の中にあるので、アバタースケールを変えると
            // ガイドごと伸縮してしまい、半径や高さがメートルとずれる。
            // ワールドスケールを 1 に固定して、指定した実寸で描かせる。
            //
            // 付けるのはこの OrbitGuide だけ。半径は Ring / TiltRing の
            // m_LocalScale で駆動しているので、そちらに付けると潰れる。
            EnsureComponent<ModularAvatarWorldScaleObject>(guide.gameObject);

            // 柱は可動範囲全体をカバーする細いシリンダー。
            //
            // 以前はパーティクル 600 粒を縦に並べて線に見せていたが、
            // VRChat のパーティクル枠（Poor で 16 システム / 2500 粒）を
            // 5 スロット分で大幅に超えていた。実体のあるメッシュなら
            // 粒を 1 つも使わず、どの方向から見ても同じ太さで見える。
            // メッシュ枠は Poor で 24 に対して余裕がある。
            var pillar = EnsureChild(guide, PillarName);
            pillar.localPosition = new Vector3(0f, (HeightMin + HeightMax) * 0.5f, 0f);
            pillar.localRotation = Quaternion.identity;
            ConfigurePillarMesh(pillar.gameObject, pillarMaterial);

            // 仮想床の中心軸。柱の根元だけでは床のどこに立っているか分かりにくいので、
            // 地面と平行に寝かせた輪を置く。高さは動かないので guide 直下でよい。
            var floorMarker = EnsureChild(guide, FloorMarkerName);
            floorMarker.localPosition = Vector3.zero;
            floorMarker.localRotation = Quaternion.identity;
            floorMarker.localScale = Vector3.one;
            ConfigureMarkPointParticle(floorMarker.gameObject, floorMaterial, FloorMarkerSize,
                ParticleSystemRenderMode.HorizontalBillboard);

            var center = EnsureChild(guide, CenterName);
            center.localPosition = new Vector3(0f, HeightDefault, 0f);
            center.localRotation = Quaternion.identity;
            center.localScale = Vector3.one;

            // Cube は既存のものがあれば取り込む
            var cube = center.Find(CubeName);
            if (cube == null)
            {
                var existing = slot.Find(CubeName);
                if (existing != null)
                {
                    Undo.SetTransformParent(existing, center, "Move Cube");
                    existing.localPosition = Vector3.zero;
                    cube = existing;
                    notes.Add($"{slot.name} の既存 Cube を {GuideRootName}/{CenterName} 配下へ移動しました。");
                }
                else
                {
                    cube = CreateCube(center);
                }
            }

            cube.localPosition = Vector3.zero;

            var marker = EnsureChild(center, MarkerName);
            marker.localPosition = Vector3.zero;
            marker.localRotation = Quaternion.identity;
            marker.localScale = Vector3.one;
            ConfigureMarkPointParticle(marker.gameObject, centerMaterial, CenterMarkerSize);

            // 旋回円は中心点とは別の高さを持てるよう、Center の外に出す
            var ringCenter = EnsureChild(guide, RingCenterName);
            ringCenter.localPosition = new Vector3(0f, HeightDefault, 0f);
            ringCenter.localRotation = Quaternion.identity;
            ringCenter.localScale = Vector3.one;

            // 水平の参照円は、傾き 0 のとき TiltRing と完全に重なる。
            // パーティクルシステム数を Poor の 16 に収めるため持たない。
            var legacyFlatRing = ringCenter.Find(RingName);
            if (legacyFlatRing != null) Undo.DestroyObjectImmediate(legacyFlatRing.gameObject);

            // 傾いた円。軸を「ユーザーから見た前後」にするため、
            // YawFollow でプレイヤーの向きに追従させてから X 回転をかける。
            var yawFollow = EnsureChild(ringCenter, YawFollowName);
            yawFollow.localPosition = Vector3.zero;
            yawFollow.localRotation = Quaternion.identity;
            yawFollow.localScale = Vector3.one;
            ConfigureYawFollow(yawFollow.gameObject, yawSource, notes);

            // 傾ける向き（最下点をどこに置くか）を Y 回転で決める
            var tiltAzimuth = EnsureChild(yawFollow, TiltAzimuthName);
            tiltAzimuth.localPosition = Vector3.zero;
            tiltAzimuth.localRotation = Quaternion.Euler(0f, TiltDirDefaultDeg, 0f);
            tiltAzimuth.localScale = Vector3.one;

            var tiltPivot = EnsureChild(tiltAzimuth, TiltPivotName);
            tiltPivot.localPosition = Vector3.zero;
            tiltPivot.localRotation = Quaternion.Euler(TiltDefaultDeg, 0f, 0f);
            tiltPivot.localScale = Vector3.one;

            var tiltRing = EnsureChild(tiltPivot, TiltRingName);
            tiltRing.localPosition = Vector3.zero;
            tiltRing.localRotation = Quaternion.identity;
            tiltRing.localScale = Vector3.one * RadiusDefault;
            ConfigureRingParticle(tiltRing.gameObject, ringMaterial);

            // X 軸まわりに傾けるので、円周上で最も高低差が出るのは局所 Z 軸上の点。
            // 半径と一緒に動かすため、位置は半径のクリップ側でアニメーションする。
            var lowPoint = EnsureChild(tiltPivot, LowPointName);
            lowPoint.localPosition = new Vector3(0f, 0f, RadiusDefault);
            lowPoint.localRotation = Quaternion.identity;
            lowPoint.localScale = Vector3.one;
            ConfigureMarkPointParticle(lowPoint.gameObject, lowPointMaterial, LowPointSize);

            // 旧構成（TiltPivot が YawFollow 直下）の残骸を掃除する
            var legacyPivot = yawFollow.Find(TiltPivotName);
            if (legacyPivot != null && legacyPivot != tiltPivot)
            {
                Undo.DestroyObjectImmediate(legacyPivot.gameObject);
            }

            // 1本構成だった頃の Ring が Center 配下に残っていたら掃除する
            var legacyRing = center.Find(RingName);
            if (legacyRing != null) Undo.DestroyObjectImmediate(legacyRing.gameObject);

            return guide;
        }

        /// <summary>
        /// 向きだけプレイヤーに追従させる（Y軸のみ）。位置は変えない。
        /// Sources の自動設定に失敗した場合は手作業に回す。
        /// </summary>
        private static void ConfigureYawFollow(GameObject go, Transform yawSource, List<string> notes)
        {
            var constraint = EnsureComponent<VRCRotationConstraint>(go);
            using (var so = new SerializedObject(constraint))
            {
                SetBoolProperty(so, true, "IsActive", "IsContraintActive");
                SetBoolProperty(so, true, "Locked", "IsLocked");
                SetBoolProperty(so, false, "AffectsRotationX", "AffectRotationX");
                SetBoolProperty(so, true, "AffectsRotationY", "AffectRotationY");
                SetBoolProperty(so, false, "AffectsRotationZ", "AffectRotationZ");
                so.ApplyModifiedPropertiesWithoutUndo();
            }

            if (!TrySetSourceByReflection(constraint, yawSource))
            {
                notes.Add($"{go.name} の VRC Rotation Constraint の Sources に " +
                          $"'{yawSource.name}' を手で設定してください。");
            }

            EditorUtility.SetDirty(constraint);
        }

        private static void SetBoolProperty(SerializedObject so, bool value, params string[] names)
        {
            foreach (var name in names)
            {
                var property = so.FindProperty(name);
                if (property == null || property.propertyType != SerializedPropertyType.Boolean) continue;
                property.boolValue = value;
                return;
            }
        }

        /// <summary>
        /// Sources は VRCConstraintSourceKeyableList 型で、配列ではなく
        /// source0〜source15 の平坦なフィールドを持つ。シリアライズ名を当てにいくより
        /// 公開 API をリフレクションで叩く方が確実。
        /// </summary>
        private static bool TrySetSourceByReflection(Component constraint, Transform source)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            var sourcesProperty = constraint.GetType().GetProperty("Sources", flags);
            var list = sourcesProperty?.GetValue(constraint);
            if (list == null) return false;

            var listType = list.GetType();
            Type elementType = null;
            foreach (var iface in listType.GetInterfaces())
            {
                if (!iface.IsGenericType || iface.GetGenericTypeDefinition() != typeof(IList<>)) continue;
                elementType = iface.GetGenericArguments()[0];
                break;
            }

            if (elementType == null) return false;

            // 既に入っていれば触らない
            if (list is System.Collections.IEnumerable enumerable)
            {
                foreach (var entry in enumerable)
                {
                    if (entry != null && ReadTransform(elementType, entry) == source) return true;
                }
            }

            var element = System.Activator.CreateInstance(elementType);
            if (!AssignMember(elementType, element, typeof(Transform), source)) return false;
            AssignMember(elementType, element, typeof(float), 1f);

            var add = listType.GetMethod("Add", flags, null, new[] { elementType }, null);
            if (add == null) return false;

            try
            {
                add.Invoke(list, new object[] { element });
            }
            catch (Exception)
            {
                return false;
            }

            if (listType.IsValueType && sourcesProperty.CanWrite) sourcesProperty.SetValue(constraint, list);
            return true;
        }

        private static Transform ReadTransform(Type type, object instance)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var property in type.GetProperties(flags))
            {
                if (property.CanRead && typeof(Transform).IsAssignableFrom(property.PropertyType))
                    return property.GetValue(instance) as Transform;
            }

            foreach (var field in type.GetFields(flags))
            {
                if (typeof(Transform).IsAssignableFrom(field.FieldType))
                    return field.GetValue(instance) as Transform;
            }

            return null;
        }

        private static bool AssignMember(Type type, object target, Type valueType, object value)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;

            foreach (var property in type.GetProperties(flags))
            {
                if (!property.CanWrite || !property.PropertyType.IsAssignableFrom(valueType)) continue;
                property.SetValue(target, value);
                return true;
            }

            foreach (var field in type.GetFields(flags))
            {
                if (!field.FieldType.IsAssignableFrom(valueType)) continue;
                field.SetValue(target, value);
                return true;
            }

            return false;
        }

        private static Transform CreateCube(Transform parent)
        {
            var cube = GameObject.CreatePrimitive(PrimitiveType.Cube);
            cube.name = CubeName;
            Undo.RegisterCreatedObjectUndo(cube, "Create Cube");
            Undo.SetTransformParent(cube.transform, parent, "Parent Cube");
            cube.transform.localPosition = Vector3.zero;
            cube.transform.localRotation = Quaternion.identity;
            cube.transform.localScale = Vector3.one * 0.1f;

            // 当たり判定は不要
            var collider = cube.GetComponent<Collider>();
            if (collider != null) Undo.DestroyObjectImmediate(collider);

            return cube.transform;
        }

        // -------------------------------------------------------------------
        // パーティクル
        // -------------------------------------------------------------------

        private static ParticleSystem EnsureParticle(GameObject go, Material material,
            float size, float lifetime, float rate, int maxParticles,
            ParticleSystemRenderMode renderMode = ParticleSystemRenderMode.Billboard)
        {
            var ps = go.GetComponent<ParticleSystem>();
            if (ps == null) ps = Undo.AddComponent<ParticleSystem>(go);

            var main = ps.main;
            main.loop = true;
            main.playOnAwake = true;
            main.startLifetime = lifetime;
            main.startSpeed = 0f;
            main.startSize = size;
            main.startColor = Color.white;
            main.gravityModifier = 0f;
            main.maxParticles = maxParticles;
            main.simulationSpace = ParticleSystemSimulationSpace.Local;
            // 粒の大きさは変えず、形だけ Transform のスケールに追従させる
            main.scalingMode = ParticleSystemScalingMode.Shape;

            var emission = ps.emission;
            emission.enabled = true;
            emission.rateOverTime = rate;

            var renderer = go.GetComponent<ParticleSystemRenderer>();
            if (renderer != null)
            {
                renderer.renderMode = renderMode;
                renderer.sharedMaterial = material;
                // Horizontal/Vertical Billboard は自前で向きを決めるので
                // alignment は効かない。Billboard のときだけ意味を持つ。
                renderer.alignment = ParticleSystemRenderSpace.View;
                renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                renderer.receiveShadows = false;
                renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
                renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;
            }

            return ps;
        }

        /// <summary>
        /// 中心の柱。細いシリンダーのメッシュで描く。
        ///
        /// Unity の Cylinder プリミティブは高さ 2 なので、Y スケールは
        /// 必要な長さの半分を入れる。コライダーは不要なので消す。
        /// </summary>
        private static void ConfigurePillarMesh(GameObject go, Material material)
        {
            // 古い構成のパーティクルが残っていたら消す
            var oldPs = go.GetComponent<ParticleSystem>();
            if (oldPs != null) Undo.DestroyObjectImmediate(oldPs);
            var oldPsr = go.GetComponent<ParticleSystemRenderer>();
            if (oldPsr != null) Undo.DestroyObjectImmediate(oldPsr);

            var filter = EnsureComponent<MeshFilter>(go);
            if (filter.sharedMesh == null || filter.sharedMesh.name != "Cylinder")
            {
                var temp = GameObject.CreatePrimitive(PrimitiveType.Cylinder);
                filter.sharedMesh = temp.GetComponent<MeshFilter>().sharedMesh;
                UnityEngine.Object.DestroyImmediate(temp);
            }

            var renderer = EnsureComponent<MeshRenderer>(go);
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;
            renderer.lightProbeUsage = UnityEngine.Rendering.LightProbeUsage.Off;
            renderer.reflectionProbeUsage = UnityEngine.Rendering.ReflectionProbeUsage.Off;

            var collider = go.GetComponent<Collider>();
            if (collider != null) Undo.DestroyObjectImmediate(collider);

            go.transform.localScale = new Vector3(
                PillarThickness, (HeightMax - HeightMin) * 0.5f, PillarThickness);
        }

        /// <summary>
        /// 位置を指す印。テクスチャを貼った 1 粒だけで描く。
        ///
        /// 以前は 60 粒を重ねて塊に見せていたが、絵を貼れば 1 粒で足りる。
        /// ビルボードなのでどの向きからでも同じ見え方になる。
        /// </summary>
        private static void ConfigureMarkPointParticle(GameObject go, Material material, float size,
            ParticleSystemRenderMode renderMode = ParticleSystemRenderMode.VerticalBillboard)
        {
            // 1 粒を出したまま消さない。寿命を無限にはできないので十分長く取り、
            // 発生率ではなく Burst で 1 粒だけ出す
            var ps = EnsureParticle(go, material, size, 3600f, 0f, 1, renderMode);
            var emission = ps.emission;
            emission.rateOverTime = 0f;
            emission.SetBursts(new[] { new ParticleSystem.Burst(0f, 1) });

            var shape = ps.shape;
            shape.enabled = false;   // 原点そのものに出す
        }


        /// <summary>
        /// 旋回円。円周上に粒を撒いて破線として見せる。
        ///
        /// 寿命が残像の長さそのものになる。scalingMode が Shape なので
        /// Transform のスケールは「粒を出す位置」にしか効かず、すでに出ている粒は
        /// 寿命が尽きるまで古い半径の位置に残る。半径を動かすと軌跡が尾を引くため、
        /// 寿命は短くし、発生率を上げて密度を保つ。
        /// 発生率は VRChat の評価対象外で、効くのは最大パーティクル数だけ。
        ///
        /// 粒の大きさは半径によらず一定。半径が大きいほど間隔が開いて
        /// 破線が粗くなるが、輪郭を追う用途には足りる。
        /// </summary>
        private static void ConfigureRingParticle(GameObject go, Material material)
        {
            var ps = EnsureParticle(go, material, RingDotSize,
                RingLifetime, RingLiveCount / RingLifetime, RingMaxParticles);
            var shape = ps.shape;
            shape.enabled = true;
            shape.shapeType = ParticleSystemShapeType.Circle;
            shape.radius = 1f;          // Transform の一様スケールが実際の半径になる
            shape.radiusThickness = 0f; // 縁からのみ放出＝円周
            shape.arc = 360f;
            shape.arcMode = ParticleSystemShapeMultiModeValue.Random;
            // Circle は既定で XY 平面。X に -90 度回して床と水平にする
            shape.rotation = new Vector3(-90f, 0f, 0f);
            shape.position = Vector3.zero;
        }

        /// <remarks>
        /// 既存のマテリアルがあっても色は毎回上書きする。そうしないと配色を
        /// 変えてセットアップし直しても、前回の色のまま残る。
        /// </remarks>
        private static Material BuildGuideMaterial(string materialName, Color color)
        {
            var path = $"{AssetDir}/Materials/{materialName}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Particles/Standard Unlit")
                             ?? Shader.Find("Legacy Shaders/Particles/Additive")
                             ?? Shader.Find("Unlit/Color");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.color = color;
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// 位置を指す印のマテリアル。輪の粒とは絵柄が違うので別に持つ。
        ///
        /// テクスチャが無い環境でも動くよう、見つからなければ
        /// 無地のガイドマテリアルで代用する。
        /// </summary>
        /// <param name="texturePath">貼るテクスチャ。</param>
        /// <param name="materialName">Materials/ 配下に作るマテリアル名。</param>
        private static Material BuildMarkMaterial(string texturePath, string materialName,
            Color color)
        {
            var texture = AssetDatabase.LoadAssetAtPath<Texture2D>(texturePath);
            if (texture == null)
            {
                Debug.LogWarning($"[CamDrone Orbit] {texturePath} が見つかりません。" +
                                 "印は無地で作ります。");
                return BuildGuideMaterial(materialName, color);
            }

            ConfigureMarkTextureImport(texturePath);

            var path = $"{AssetDir}/Materials/{materialName}.mat";
            var material = AssetDatabase.LoadAssetAtPath<Material>(path);
            if (material == null)
            {
                var shader = Shader.Find("Particles/Standard Unlit")
                             ?? Shader.Find("Legacy Shaders/Particles/Alpha Blended");
                material = new Material(shader);
                AssetDatabase.CreateAsset(material, path);
            }

            material.color = color;
            material.mainTexture = texture;
            // アルファブレンド（Fade）。透過 PNG をそのまま活かす
            if (material.HasProperty("_Mode")) material.SetFloat("_Mode", 2f);
            EditorUtility.SetDirty(material);
            return material;
        }

        /// <summary>
        /// 印のテクスチャのインポート設定を整える。
        ///
        /// alphaIsTransparency を立てないと、透過部分の縁に黒い輪郭が出る。
        /// 2048 のままだとテクスチャ容量を無駄に使うので 256 に落とす。
        /// </summary>
        private static void ConfigureMarkTextureImport(string texturePath)
        {
            var importer = AssetImporter.GetAtPath(texturePath) as TextureImporter;
            if (importer == null) return;

            var changed = false;
            if (!importer.alphaIsTransparency) { importer.alphaIsTransparency = true; changed = true; }
            if (importer.maxTextureSize > 256) { importer.maxTextureSize = 256; changed = true; }
            if (importer.wrapMode != TextureWrapMode.Clamp)
            {
                importer.wrapMode = TextureWrapMode.Clamp;
                changed = true;
            }
            if (changed)
            {
                importer.SaveAndReimport();
                Debug.Log($"[CamDrone Orbit] {texturePath} のインポート設定を調整しました" +
                          "（透過の扱い / 最大 256）。");
            }
        }

        // -------------------------------------------------------------------
        // アニメーションクリップ
        // -------------------------------------------------------------------

        private sealed class GuideClips
        {
            public AnimationClip HeightMin;
            public AnimationClip HeightMax;
            public AnimationClip RingHeightMin;
            public AnimationClip RingHeightMax;
            public AnimationClip RadiusMin;
            public AnimationClip RadiusMax;
            public AnimationClip TiltMin;
            public AnimationClip TiltMax;
            public AnimationClip TiltDirMin;
            public AnimationClip TiltDirMax;
            public AnimationClip GuideOn;
            public AnimationClip GuideOff;
        }

        private static GuideClips BuildClips()
        {
            return new GuideClips
            {
                HeightMin = BuildPositionClip("Orbit_Height_Min", CenterName, HeightMin),
                HeightMax = BuildPositionClip("Orbit_Height_Max", CenterName, HeightMax),
                RingHeightMin = BuildPositionClip("Orbit_RingHeight_Min", RingCenterName, HeightMin),
                RingHeightMax = BuildPositionClip("Orbit_RingHeight_Max", RingCenterName, HeightMax),
                RadiusMin = BuildScaleClip("Orbit_Radius_Min", RadiusMin),
                RadiusMax = BuildScaleClip("Orbit_Radius_Max", RadiusMax),
                TiltMin = BuildTiltClip("Orbit_Tilt_Min", TiltMinDeg),
                TiltMax = BuildTiltClip("Orbit_Tilt_Max", TiltMaxDeg),
                TiltDirMin = BuildTiltDirClip("Orbit_TiltDir_Min", TiltDirMinDeg),
                TiltDirMax = BuildTiltDirClip("Orbit_TiltDir_Max", TiltDirMaxDeg),
                GuideOn = BuildVisibilityClip("Orbit_Guide_On", true),
                GuideOff = BuildVisibilityClip("Orbit_Guide_Off", false),
            };
        }

        private static AnimationClip BuildPositionClip(string name, string path, float y)
        {
            var clip = NewClip(name);
            clip.SetCurve(path, typeof(Transform), "m_LocalPosition.y", Constant(y));
            SaveClip(clip, name);
            return clip;
        }

        private static AnimationClip BuildScaleClip(string name, float radius)
        {
            var clip = NewClip(name);
            var curve = Constant(radius);

            clip.SetCurve(TiltRingPath, typeof(Transform), "m_LocalScale.x", curve);
            clip.SetCurve(TiltRingPath, typeof(Transform), "m_LocalScale.y", curve);
            clip.SetCurve(TiltRingPath, typeof(Transform), "m_LocalScale.z", curve);

            // 最下点の目印も円周上に保つ。目印自体は拡大させたくないので
            // スケールではなく位置で動かす
            clip.SetCurve(LowPointPath, typeof(Transform), "m_LocalPosition.z", curve);

            SaveClip(clip, name);
            return clip;
        }

        private static AnimationClip BuildTiltClip(string name, float degrees)
        {
            var clip = NewClip(name);
            // クォータニオンではなくオイラー角のバインディングを使う。
            // ±30 度の範囲なので線形補間で問題ない。
            clip.SetCurve(TiltPivotPath, typeof(Transform), "localEulerAnglesRaw.x", Constant(degrees));
            clip.SetCurve(TiltPivotPath, typeof(Transform), "localEulerAnglesRaw.y", Constant(0f));
            clip.SetCurve(TiltPivotPath, typeof(Transform), "localEulerAnglesRaw.z", Constant(0f));
            SaveClip(clip, name);
            return clip;
        }

        private static AnimationClip BuildTiltDirClip(string name, float degrees)
        {
            var clip = NewClip(name);
            clip.SetCurve(TiltAzimuthPath, typeof(Transform), "localEulerAnglesRaw.x", Constant(0f));
            clip.SetCurve(TiltAzimuthPath, typeof(Transform), "localEulerAnglesRaw.y", Constant(degrees));
            clip.SetCurve(TiltAzimuthPath, typeof(Transform), "localEulerAnglesRaw.z", Constant(0f));
            SaveClip(clip, name);
            return clip;
        }

        private static AnimationClip BuildVisibilityClip(string name, bool visible)
        {
            var clip = NewClip(name);
            var curve = Constant(visible ? 1f : 0f);
            clip.SetCurve(PillarName, typeof(GameObject), "m_IsActive", curve);
            clip.SetCurve(FloorMarkerName, typeof(GameObject), "m_IsActive", curve);
            clip.SetCurve(CenterName + "/" + MarkerName, typeof(GameObject), "m_IsActive", curve);
            clip.SetCurve(TiltRingPath, typeof(GameObject), "m_IsActive", curve);
            clip.SetCurve(LowPointPath, typeof(GameObject), "m_IsActive", curve);
            SaveClip(clip, name);
            return clip;
        }

        private static AnimationClip NewClip(string name) => new AnimationClip { name = name };

        private static AnimationCurve Constant(float value) =>
            AnimationCurve.Constant(0f, 1f / 60f, value);

        private static void SaveClip(AnimationClip clip, string name)
        {
            var path = $"{AssetDir}/Animation/{name}.anim";
            var existing = AssetDatabase.LoadAssetAtPath<AnimationClip>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);
            AssetDatabase.CreateAsset(clip, path);
        }

        // -------------------------------------------------------------------
        // Animator Controller（スロットごとに1本）
        // -------------------------------------------------------------------

        private static string HeightParam(int slot) => $"CamDrone/Obj{slot}/Height";
        private static string RingHeightParam(int slot) => $"CamDrone/Obj{slot}/RingHeight";
        private static string RadiusParam(int slot) => $"CamDrone/Obj{slot}/Radius";
        private static string TiltParam(int slot) => $"CamDrone/Obj{slot}/Tilt";
        private static string TiltDirParam(int slot) => $"CamDrone/Obj{slot}/TiltDir";
        private static string GuideParam(int slot) => $"CamDrone/Obj{slot}/Guide";
        private static string SyncParam(int slot) => $"CamDrone/Obj{slot}/RingToCenter";
        private static string PointsParam(int slot) => $"CamDrone/Obj{slot}/Points";
        private static string ClockwiseParam(int slot) => $"CamDrone/Obj{slot}/CW";
        private static string RandomParam(int slot) => $"CamDrone/Obj{slot}/Random";
        private static string ConfirmParam(int slot) => $"CamDrone/Obj{slot}/Confirm";

        // カメラ設定はスロットに属さないので Obj{N} を挟まない
        private const string ZoomParam = "CamDrone/Camera/Zoom";
        private const string FocalDistanceParam = "CamDrone/Camera/FocalDistance";
        private const string ApertureParam = "CamDrone/Camera/Aperture";
        private const string DurationParam = "CamDrone/Camera/Duration";
        private const string SpeedParam = "CamDrone/Camera/Speed";
        private const string ResetZoomParam = "CamDrone/Camera/ResetZoom";
        private const string ResetFocalDistanceParam = "CamDrone/Camera/ResetFocalDistance";
        private const string ResetApertureParam = "CamDrone/Camera/ResetAperture";
        private const string ResetDurationParam = "CamDrone/Camera/ResetDuration";
        private const string ResetSpeedParam = "CamDrone/Camera/ResetSpeed";

        /// <summary>
        /// カメラ設定用のコントローラ。スロットに属さないので1つだけ作る。
        ///
        /// 値そのものは何もアニメーションさせない。初期化ボタンで既定値へ戻すための
        /// Parameter Driver を置くためだけにレイヤーが要る。
        /// </summary>
        private static AnimatorController BuildCameraController()
        {
            var path = $"{AssetDir}/Animator/CamDroneOrbit_Camera.controller";
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.parameters = new[]
            {
                FloatParameter(ZoomParam, Normalize(ZoomDefault, ZoomMin, ZoomMax)),
                FloatParameter(FocalDistanceParam,
                    Normalize(FocalDistanceDefault, FocalDistanceMin, FocalDistanceMax)),
                FloatParameter(ApertureParam,
                    Normalize(ApertureDefault, ApertureMin, ApertureMax)),
                FloatParameter(DurationParam, Normalize(DurationDefault, DurationMin, DurationMax)),
                FloatParameter(SpeedParam, Normalize(SpeedDefault, SpeedMin, SpeedMax)),
                BoolParameter(ResetZoomParam, false),
                BoolParameter(ResetFocalDistanceParam, false),
                BoolParameter(ResetApertureParam, false),
                BoolParameter(ResetDurationParam, false),
                BoolParameter(ResetSpeedParam, false),
            };

            // 既定で作られる空の Base Layer を初期化用に作り替える
            var layer = controller.layers[0];
            layer.name = "CameraReset";
            layer.defaultWeight = 1f;
            controller.layers = new[] { layer };

            var machine = layer.stateMachine;
            var idle = machine.AddState("Idle");
            idle.writeDefaultValues = true;
            machine.defaultState = idle;

            AddResetState(machine, idle, "SetZoom", ResetZoomParam,
                ZoomParam, Normalize(ZoomDefault, ZoomMin, ZoomMax));
            AddResetState(machine, idle, "SetFocalDistance", ResetFocalDistanceParam,
                FocalDistanceParam,
                Normalize(FocalDistanceDefault, FocalDistanceMin, FocalDistanceMax));
            AddResetState(machine, idle, "SetAperture", ResetApertureParam,
                ApertureParam, Normalize(ApertureDefault, ApertureMin, ApertureMax));
            AddResetState(machine, idle, "SetDuration", ResetDurationParam,
                DurationParam, Normalize(DurationDefault, DurationMin, DurationMax));
            AddResetState(machine, idle, "SetSpeed", ResetSpeedParam,
                SpeedParam, Normalize(SpeedDefault, SpeedMin, SpeedMax));

            EditorUtility.SetDirty(controller);
            return controller;
        }

        /// <summary>ボタンを押している間だけ入り、対象を既定値へ書き戻すステート。</summary>
        private static void AddResetState(AnimatorStateMachine machine, AnimatorState idle,
            string stateName, string buttonParam, string targetParam, float defaultValue)
        {
            var state = machine.AddState(stateName);
            state.writeDefaultValues = true;

            var driver = state.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = true;
            driver.parameters = new List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
            {
                new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
                {
                    type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Set,
                    name = targetParam,
                    value = defaultValue,
                },
            };

            var press = idle.AddTransition(state);
            press.hasExitTime = false;
            press.duration = 0f;
            press.AddCondition(AnimatorConditionMode.If, 0f, buttonParam);

            var release = state.AddTransition(idle);
            release.hasExitTime = false;
            release.duration = 0f;
            release.AddCondition(AnimatorConditionMode.IfNot, 0f, buttonParam);
        }

        private static AnimatorController BuildController(int slot, GuideClips clips)
        {
            var path = $"{AssetDir}/Animator/CamDroneOrbit_Obj{slot}.controller";
            var existing = AssetDatabase.LoadAssetAtPath<AnimatorController>(path);
            if (existing != null) AssetDatabase.DeleteAsset(path);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            controller.parameters = new[]
            {
                FloatParameter(HeightParam(slot), Normalize(HeightDefault, HeightMin, HeightMax)),
                FloatParameter(RingHeightParam(slot), Normalize(HeightDefault, HeightMin, HeightMax)),
                FloatParameter(RadiusParam(slot), Normalize(RadiusDefault, RadiusMin, RadiusMax)),
                FloatParameter(TiltParam(slot), Normalize(TiltDefaultDeg, TiltMinDeg, TiltMaxDeg)),
                FloatParameter(TiltDirParam(slot), Normalize(TiltDirDefaultDeg, TiltDirMinDeg, TiltDirMaxDeg)),
                BoolParameter(GuideParam(slot), true),
                BoolParameter(SyncParam(slot), false),
                // 以下はアニメーションには使わず、OSC へ出すためだけに存在する
                IntParameter(PointsParam(slot), PointsDefault),
                IntParameter(RandomParam(slot), RandomDefault),
                BoolParameter(ClockwiseParam(slot), ClockwiseDefault),
                BoolParameter(ConfirmParam(slot), false),
                BoolParameter("IsLocal", false),
            };

            // 既定で作られる空の Base Layer を高さ用に作り替える
            var baseLayer = controller.layers[0];
            baseLayer.name = "Height";
            baseLayer.defaultWeight = 1f;
            controller.layers = new[] { baseLayer };
            FillBlendLayer(controller, baseLayer, HeightParam(slot), clips.HeightMin, clips.HeightMax);

            AddBlendLayer(controller, "RingHeight", RingHeightParam(slot),
                clips.RingHeightMin, clips.RingHeightMax);
            AddBlendLayer(controller, "Radius", RadiusParam(slot), clips.RadiusMin, clips.RadiusMax);
            AddBlendLayer(controller, "Tilt", TiltParam(slot), clips.TiltMin, clips.TiltMax);
            AddBlendLayer(controller, "TiltDir", TiltDirParam(slot), clips.TiltDirMin, clips.TiltDirMax);
            AddGuideLayer(controller, slot, clips);
            AddSyncLayer(controller, slot);

            EditorUtility.SetDirty(controller);
            return controller;
        }

        private static AnimatorControllerParameter FloatParameter(string name, float value) =>
            new AnimatorControllerParameter
            {
                name = name, type = AnimatorControllerParameterType.Float, defaultFloat = value
            };

        private static AnimatorControllerParameter IntParameter(string name, int value) =>
            new AnimatorControllerParameter
            {
                name = name, type = AnimatorControllerParameterType.Int, defaultInt = value
            };

        private static AnimatorControllerParameter BoolParameter(string name, bool value) =>
            new AnimatorControllerParameter
            {
                name = name, type = AnimatorControllerParameterType.Bool, defaultBool = value
            };

        private static float Normalize(float value, float min, float max) => (value - min) / (max - min);

        private static void FillBlendLayer(AnimatorController controller, AnimatorControllerLayer layer,
            string parameter, AnimationClip min, AnimationClip max)
        {
            var tree = new BlendTree
            {
                name = parameter,
                blendType = BlendTreeType.Simple1D,
                blendParameter = parameter,
                useAutomaticThresholds = false,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(tree, controller);
            tree.AddChild(min, 0f);
            tree.AddChild(max, 1f);

            var state = layer.stateMachine.AddState("Blend");
            state.motion = tree;
            state.writeDefaultValues = true;
            layer.stateMachine.defaultState = state;
        }

        private static void AddBlendLayer(AnimatorController controller, string name,
            string parameter, AnimationClip min, AnimationClip max)
        {
            var layer = NewLayer(controller, name);
            FillBlendLayer(controller, layer, parameter, min, max);
            controller.AddLayer(layer);
        }

        /// <summary>
        /// IsLocal と Guide の AND で表示を切り替える。
        /// MA の ObjectToggle は単一パラメータしか見られないのでここだけ手書きする。
        /// </summary>
        private static void AddGuideLayer(AnimatorController controller, int slot, GuideClips clips)
        {
            var layer = NewLayer(controller, "Guide");
            var machine = layer.stateMachine;

            var hidden = machine.AddState("Hidden");
            hidden.motion = clips.GuideOff;
            hidden.writeDefaultValues = true;

            var visible = machine.AddState("Visible");
            visible.motion = clips.GuideOn;
            visible.writeDefaultValues = true;

            machine.defaultState = hidden;

            var show = hidden.AddTransition(visible);
            show.hasExitTime = false;
            show.duration = 0f;
            show.AddCondition(AnimatorConditionMode.If, 0f, "IsLocal");
            show.AddCondition(AnimatorConditionMode.If, 0f, GuideParam(slot));

            // どちらかが落ちたら隠す
            var hideByLocal = visible.AddTransition(hidden);
            hideByLocal.hasExitTime = false;
            hideByLocal.duration = 0f;
            hideByLocal.AddCondition(AnimatorConditionMode.IfNot, 0f, "IsLocal");

            var hideByToggle = visible.AddTransition(hidden);
            hideByToggle.hasExitTime = false;
            hideByToggle.duration = 0f;
            hideByToggle.AddCondition(AnimatorConditionMode.IfNot, 0f, GuideParam(slot));

            controller.AddLayer(layer);
        }

        /// <summary>
        /// 円の高さを中心点の高さに合わせ直すボタン用のレイヤー。
        /// Height と RingHeight は同じ範囲を 0〜1 に正規化しているので、
        /// Parameter Driver の Copy をそのまま使える（範囲変換は不要）。
        /// </summary>
        private static void AddSyncLayer(AnimatorController controller, int slot)
        {
            var layer = NewLayer(controller, "RingToCenter");
            var machine = layer.stateMachine;

            var idle = machine.AddState("Idle");
            idle.writeDefaultValues = true;
            machine.defaultState = idle;

            var apply = machine.AddState("Apply");
            apply.writeDefaultValues = true;

            var driver = apply.AddStateMachineBehaviour<VRCAvatarParameterDriver>();
            driver.localOnly = true;
            driver.parameters = new List<VRC.SDKBase.VRC_AvatarParameterDriver.Parameter>
            {
                new VRC.SDKBase.VRC_AvatarParameterDriver.Parameter
                {
                    type = VRC.SDKBase.VRC_AvatarParameterDriver.ChangeType.Copy,
                    source = HeightParam(slot),
                    name = RingHeightParam(slot),
                    convertRange = false,
                },
            };

            var press = idle.AddTransition(apply);
            press.hasExitTime = false;
            press.duration = 0f;
            press.AddCondition(AnimatorConditionMode.If, 0f, SyncParam(slot));

            var release = apply.AddTransition(idle);
            release.hasExitTime = false;
            release.duration = 0f;
            release.AddCondition(AnimatorConditionMode.IfNot, 0f, SyncParam(slot));

            controller.AddLayer(layer);
        }

        private static AnimatorControllerLayer NewLayer(AnimatorController controller, string name)
        {
            var machine = new AnimatorStateMachine
            {
                name = name,
                hideFlags = HideFlags.HideInHierarchy,
            };
            AssetDatabase.AddObjectToAsset(machine, controller);

            return new AnimatorControllerLayer
            {
                name = name,
                defaultWeight = 1f,
                stateMachine = machine,
            };
        }

        // -------------------------------------------------------------------
        // Expression Menu
        // -------------------------------------------------------------------

        private static VRCExpressionsMenu BuildSlotMenu(int slot)
        {
            var menu = NewMenu($"CamDroneOrbit_Obj{slot}");
            menu.controls = new List<VRCExpressionsMenu.Control>
            {
                RadialControl("Center Height", HeightParam(slot)),
                RadialControl("Ring Height", RingHeightParam(slot)),
                ButtonControl("Ring -> Center", SyncParam(slot)),
                RadialControl("Radius", RadiusParam(slot)),
                SubMenuControl("Tilt", BuildTiltMenu(slot)),
                SubMenuControl("Path", BuildPathMenu(slot)),
                ToggleControl("Guide", GuideParam(slot)),
                ButtonControl("Confirm", ConfirmParam(slot)),
            };
            EditorUtility.SetDirty(menu);
            return menu;
        }

        /// <summary>
        /// 傾きの「角度」と「向き」。向きは最下点の目印を円周に沿って回す操作にあたる。
        /// </summary>
        private static VRCExpressionsMenu BuildTiltMenu(int slot)
        {
            var menu = NewMenu($"CamDroneOrbit_Obj{slot}_Tilt");
            menu.controls = new List<VRCExpressionsMenu.Control>
            {
                RadialControl("Angle", TiltParam(slot)),
                RadialControl("Low Point", TiltDirParam(slot)),
            };
            EditorUtility.SetDirty(menu);
            return menu;
        }

        /// <summary>
        /// 軌道の作り方に関する設定をまとめたメニュー。
        /// いずれもアバター側では何も動かさず、PC 側の生成にだけ効く。
        /// </summary>
        private static VRCExpressionsMenu BuildPathMenu(int slot)
        {
            var menu = NewMenu($"CamDroneOrbit_Obj{slot}_Path");
            var controls = new List<VRCExpressionsMenu.Control>
            {
                SubMenuControl("Points", BuildPointsMenu(slot)),
                // ON で時計回り（右回り）、OFF で反時計回り（左回り）
                ToggleControl("右回り", ClockwiseParam(slot)),
            };

            foreach (var percent in RandomChoices)
            {
                var label = percent == 0 ? "ランダム なし" : $"ランダム ±{percent}%";
                controls.Add(new VRCExpressionsMenu.Control
                {
                    name = label,
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = RandomParam(slot) },
                    value = percent,
                    subParameters = new VRCExpressionsMenu.Control.Parameter[0],
                    labels = new VRCExpressionsMenu.Control.Label[0],
                });
            }

            menu.controls = controls;
            EditorUtility.SetDirty(menu);
            return menu;
        }

        /// <summary>
        /// 1周あたりのポイント数を選ぶメニュー。同じ Int パラメータに対して
        /// 値の違うトグルを並べる、いわゆるラジオボタンの作り方。
        /// </summary>
        private static VRCExpressionsMenu BuildPointsMenu(int slot)
        {
            var menu = NewMenu($"CamDroneOrbit_Obj{slot}_Points");
            var controls = new List<VRCExpressionsMenu.Control>();
            foreach (var points in PointChoices)
            {
                controls.Add(new VRCExpressionsMenu.Control
                {
                    name = points.ToString(),
                    type = VRCExpressionsMenu.Control.ControlType.Toggle,
                    parameter = new VRCExpressionsMenu.Control.Parameter { name = PointsParam(slot) },
                    value = points,
                    subParameters = new VRCExpressionsMenu.Control.Parameter[0],
                    labels = new VRCExpressionsMenu.Control.Label[0],
                });
            }

            menu.controls = controls;
            EditorUtility.SetDirty(menu);
            return menu;
        }

        private static VRCExpressionsMenu BuildRootMenu(VRCExpressionsMenu[] subMenus)
        {
            var menu = NewMenu("CamDroneOrbit_Root");
            menu.controls = new List<VRCExpressionsMenu.Control>
            {
                SubMenuControl("Pivot", BuildPivotMenu(subMenus)),
                SubMenuControl("Camera", BuildCameraMenu()),
            };
            EditorUtility.SetDirty(menu);
            return menu;
        }

        /// <summary>旋回の中心にする固定点を選ぶ。中身は FloorPointer の Object_N。</summary>
        private static VRCExpressionsMenu BuildPivotMenu(VRCExpressionsMenu[] subMenus)
        {
            var menu = NewMenu("CamDroneOrbit_Pivot");
            var controls = new List<VRCExpressionsMenu.Control>();
            for (var i = 0; i < subMenus.Length; i++)
            {
                controls.Add(SubMenuControl("Pivot " + (i + 1), subMenus[i]));
            }

            menu.controls = controls;
            EditorUtility.SetDirty(menu);
            return menu;
        }

        /// <summary>
        /// 生成する JSON へ書くカメラ設定。スロットに属さないので根に置く。
        ///
        /// 1階層に置けるのは 8 個まで。項目ごとに初期化ボタンを付けると 10 個に
        /// なってしまうので、意味で Lens と Motion に分けている。
        /// </summary>
        private static VRCExpressionsMenu BuildCameraMenu()
        {
            var menu = NewMenu("CamDroneOrbit_Camera");
            menu.controls = new List<VRCExpressionsMenu.Control>
            {
                SubMenuControl("Lens", BuildLensMenu()),
                SubMenuControl("Motion", BuildMotionMenu()),
            };
            EditorUtility.SetDirty(menu);
            return menu;
        }

        /// <summary>
        /// 画づくりの設定。
        ///
        /// パペットは % での大まかな操作しかできず既定値ちょうどには戻せないため、
        /// 項目ごとに初期化ボタンを添えてある。
        /// </summary>
        private static VRCExpressionsMenu BuildLensMenu()
        {
            var menu = NewMenu("CamDroneOrbit_Lens");
            menu.controls = new List<VRCExpressionsMenu.Control>
            {
                RadialControl("Zoom", ZoomParam),
                ButtonControl("Zoom 初期化", ResetZoomParam),
                RadialControl("FocalDistance", FocalDistanceParam),
                ButtonControl("FocalDistance 初期化", ResetFocalDistanceParam),
                RadialControl("Aperture", ApertureParam),
                ButtonControl("Aperture 初期化", ResetApertureParam),
            };
            EditorUtility.SetDirty(menu);
            return menu;
        }

        /// <summary>再生の速さに関する設定。</summary>
        private static VRCExpressionsMenu BuildMotionMenu()
        {
            var menu = NewMenu("CamDroneOrbit_Motion");
            menu.controls = new List<VRCExpressionsMenu.Control>
            {
                RadialControl("Duration", DurationParam),
                ButtonControl("Duration 初期化", ResetDurationParam),
                RadialControl("Speed", SpeedParam),
                ButtonControl("Speed 初期化", ResetSpeedParam),
            };
            EditorUtility.SetDirty(menu);
            return menu;
        }

        private static VRCExpressionsMenu NewMenu(string name)
        {
            var path = $"{AssetDir}/Expression/{name}.asset";
            var existing = AssetDatabase.LoadAssetAtPath<VRCExpressionsMenu>(path);
            if (existing != null) return existing;

            var menu = ScriptableObject.CreateInstance<VRCExpressionsMenu>();
            menu.name = name;
            AssetDatabase.CreateAsset(menu, path);
            return menu;
        }

        private static VRCExpressionsMenu.Control RadialControl(string label, string parameter) =>
            new VRCExpressionsMenu.Control
            {
                name = label,
                type = VRCExpressionsMenu.Control.ControlType.RadialPuppet,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "" },
                value = 1f,
                subParameters = new[] { new VRCExpressionsMenu.Control.Parameter { name = parameter } },
                labels = new VRCExpressionsMenu.Control.Label[0],
            };

        private static VRCExpressionsMenu.Control SubMenuControl(string label, VRCExpressionsMenu subMenu) =>
            new VRCExpressionsMenu.Control
            {
                name = label,
                type = VRCExpressionsMenu.Control.ControlType.SubMenu,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = "" },
                subMenu = subMenu,
                subParameters = new VRCExpressionsMenu.Control.Parameter[0],
                labels = new VRCExpressionsMenu.Control.Label[0],
            };

        /// <summary>押している間だけ true になる。一回きりの操作向け。</summary>
        private static VRCExpressionsMenu.Control ButtonControl(string label, string parameter) =>
            new VRCExpressionsMenu.Control
            {
                name = label,
                type = VRCExpressionsMenu.Control.ControlType.Button,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
                value = 1f,
                subParameters = new VRCExpressionsMenu.Control.Parameter[0],
                labels = new VRCExpressionsMenu.Control.Label[0],
            };

        private static VRCExpressionsMenu.Control ToggleControl(string label, string parameter) =>
            new VRCExpressionsMenu.Control
            {
                name = label,
                type = VRCExpressionsMenu.Control.ControlType.Toggle,
                parameter = new VRCExpressionsMenu.Control.Parameter { name = parameter },
                value = 1f,
                subParameters = new VRCExpressionsMenu.Control.Parameter[0],
                labels = new VRCExpressionsMenu.Control.Label[0],
            };

        // -------------------------------------------------------------------
        // Modular Avatar
        // -------------------------------------------------------------------

        private static void ConfigureMergeAnimator(GameObject go, RuntimeAnimatorController controller)
        {
            var merge = EnsureComponent<ModularAvatarMergeAnimator>(go);
            Undo.RecordObject(merge, "Configure Merge Animator");
            merge.animator = controller;
            merge.layerType = VRCAvatarDescriptor.AnimLayerType.FX;
            merge.deleteAttachedAnimator = false;
            // クリップ内のパスは OrbitGuide からの相対で書いてある
            merge.pathMode = MergeAnimatorPathMode.Relative;
            merge.matchAvatarWriteDefaults = true;
            EditorUtility.SetDirty(merge);
        }

        private static void ConfigureParameters(GameObject go, int slot)
        {
            var parameters = EnsureComponent<ModularAvatarParameters>(go);
            Undo.RecordObject(parameters, "Configure Parameters");

            var heightDefault = Normalize(HeightDefault, HeightMin, HeightMax);
            parameters.parameters = new List<ParameterConfig>
            {
                Param(HeightParam(slot), ParameterSyncType.Float, heightDefault),
                Param(RingHeightParam(slot), ParameterSyncType.Float, heightDefault),
                Param(RadiusParam(slot), ParameterSyncType.Float, Normalize(RadiusDefault, RadiusMin, RadiusMax)),
                Param(TiltParam(slot), ParameterSyncType.Float, Normalize(TiltDefaultDeg, TiltMinDeg, TiltMaxDeg)),
                Param(TiltDirParam(slot), ParameterSyncType.Float,
                    Normalize(TiltDirDefaultDeg, TiltDirMinDeg, TiltDirMaxDeg)),
                // 既定 1（表示）で問題ない。他人に見えないことは Guide レイヤーの
                // IsLocal 条件が保証しており、既定値には依存しない。
                Param(GuideParam(slot), ParameterSyncType.Bool, 1f),
                Param(PointsParam(slot), ParameterSyncType.Int, PointsDefault),
                Param(RandomParam(slot), ParameterSyncType.Int, RandomDefault),
                Param(ClockwiseParam(slot), ParameterSyncType.Bool, ClockwiseDefault ? 1f : 0f),
                Param(SyncParam(slot), ParameterSyncType.Bool, 0f),
                Param(ConfirmParam(slot), ParameterSyncType.Bool, 0f),
            };

            EditorUtility.SetDirty(parameters);
        }

        private static void ConfigureCameraParameters(GameObject go)
        {
            var parameters = EnsureComponent<ModularAvatarParameters>(go);
            Undo.RecordObject(parameters, "Configure Camera Parameters");

            parameters.parameters = new List<ParameterConfig>
            {
                Param(ZoomParam, ParameterSyncType.Float, Normalize(ZoomDefault, ZoomMin, ZoomMax)),
                Param(FocalDistanceParam, ParameterSyncType.Float,
                    Normalize(FocalDistanceDefault, FocalDistanceMin, FocalDistanceMax)),
                Param(ApertureParam, ParameterSyncType.Float,
                    Normalize(ApertureDefault, ApertureMin, ApertureMax)),
                Param(DurationParam, ParameterSyncType.Float,
                    Normalize(DurationDefault, DurationMin, DurationMax)),
                Param(SpeedParam, ParameterSyncType.Float, Normalize(SpeedDefault, SpeedMin, SpeedMax)),
                Param(ResetZoomParam, ParameterSyncType.Bool, 0f),
                Param(ResetFocalDistanceParam, ParameterSyncType.Bool, 0f),
                Param(ResetApertureParam, ParameterSyncType.Bool, 0f),
                Param(ResetDurationParam, ParameterSyncType.Bool, 0f),
                Param(ResetSpeedParam, ParameterSyncType.Bool, 0f),
            };

            EditorUtility.SetDirty(parameters);
        }

        /// <summary>
        /// Synced は OFF（localOnly）。調整するのは自分だけで、他プレイヤーに
        /// 同期する必要がないため、同期ビット(256bit)を消費しない。
        ///
        /// saved も OFF。前回の値が残っていると、意図していない設定のまま
        /// 生成してしまい、しかも正常に動いているように見える。
        /// アバターを読み込むたびに既定値から始める。
        /// </summary>
        private static ParameterConfig Param(string name, ParameterSyncType type, float defaultValue,
            bool saved = false) =>
            new ParameterConfig
            {
                nameOrPrefix = name,
                remapTo = "",
                internalParameter = false,
                isPrefix = false,
                syncType = type,
                localOnly = true,
                defaultValue = defaultValue,
                hasExplicitDefaultValue = true,
                saved = saved,
            };

        private static void ConfigureMenuInstaller(GameObject go, VRCExpressionsMenu menu)
        {
            var installer = EnsureComponent<ModularAvatarMenuInstaller>(go);
            Undo.RecordObject(installer, "Configure Menu Installer");
            installer.menuToAppend = menu;
            installer.installTargetMenu = null; // アバターのルートメニューへ
            EditorUtility.SetDirty(installer);
        }

        // -------------------------------------------------------------------
        // 共通
        // -------------------------------------------------------------------

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
            var component = go.GetComponent<T>();
            return component != null ? component : Undo.AddComponent<T>(go);
        }

        private static void EnsureDirectory(string path)
        {
            if (Directory.Exists(path)) return;
            Directory.CreateDirectory(path);
            AssetDatabase.Refresh();
        }
    }
}
