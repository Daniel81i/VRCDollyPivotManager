#!/usr/bin/env python3
"""VRCDollyPivotManager.py

VR 内で床に付けた印を中心に回る、VRChat Camera Dolly のパスを作る常駐ツール。

アバター側のレイが印までの距離を3方向から測って OSC で送ってくる。
Confirm を受けた時点の値から三辺測量で印の位置を求め、そこを軸にした
旋回パスを書き出して VRChat へ読み込ませる。

コンソールを持たず、タスクトレイに常駐する。
設定は実行ファイルと同じフォルダの config.json から読む
（カレントディレクトリではなく、実行ファイルが置かれた場所）。
"""

from __future__ import annotations

import json
import math
import os
import random
import sys
import threading
import time
from collections import deque
from datetime import datetime
from pathlib import Path
from typing import Any, Deque, Dict, List, Optional, Tuple

from pythonosc import dispatcher as osc_dispatcher
from pythonosc import osc_server
from pythonosc import udp_client

from PIL import Image, ImageDraw
import pystray

import oscquery


# ---------------------------------------------------------------------------
# 配置とパス
# ---------------------------------------------------------------------------

def app_dir() -> Path:
    """実行ファイルが置かれているフォルダ。カレントディレクトリではない。"""
    if getattr(sys, "frozen", False):
        return Path(sys.executable).resolve().parent
    return Path(__file__).resolve().parent


def app_name() -> str:
    if getattr(sys, "frozen", False):
        return Path(sys.executable).stem
    return Path(__file__).stem


APP_DIR = app_dir()
APP_NAME = app_name()
CONFIG_PATH = APP_DIR / "config.json"
LOG_DIR = APP_DIR / "log"

# 生成した JSON の控え。config の output_dir とは別に、必ずここにも同じものを残す。
# 出力先を書き換えても手元に履歴が残るようにするため。
DATA_DIR = APP_DIR / "data"


# ---------------------------------------------------------------------------
# アバターと一致必須の定数
#
# 以下はアバター側にも同じ値が置いてある。仕様として固定で、config からは
# 変えられない。片方だけ書き換えると生成結果が静かにずれるため。
#
# アバター側の値を変えたときは、必ずこちらも同じ値に直してビルドし直すこと。
# 対応表は README の「アバターと一致必須の値」にある。
# ---------------------------------------------------------------------------

# プローブ3点の間隔(m)。A=(0,0) B=(baseline,0) C=(0,baseline) に置く。
# 三辺測量の分母なので、食い違うと距離が丸ごと圧縮/拡大される。
# Unity 側で Object_N/ProbeRig/ProbeB の Position X を見れば実際の値が分かる。
#
# ProbeRig には MA World Scale Object が付いていてワールドスケールが 1 に
# 固定されるので、アバタースケールを変えてもこの値は変わらない。
PROBE_BASELINE = 1.5    # CamDronePlayerProbeSetup.Baseline

HEIGHT_MIN = -1.5       # CamDroneOrbitGuideSetup.HeightMin
HEIGHT_MAX = 4.0        # CamDroneOrbitGuideSetup.HeightMax
RADIUS_MIN = 1.0        # CamDroneOrbitGuideSetup.RadiusMin
RADIUS_MAX = 25.0       # CamDroneOrbitGuideSetup.RadiusMax
TILT_MIN = -30.0        # CamDroneOrbitGuideSetup.TiltMinDeg
TILT_MAX = 30.0         # CamDroneOrbitGuideSetup.TiltMaxDeg

# 最下点の方位（メニュー上の名前は Low Point）。
# パラメータ 0〜1 を一周ではなく「右→奥→左」の半周に割り当てる。
# 手前側の半周は Tilt の符号を反転すれば出せるので、一周させる必要がない。
#
#   0%   =  +90 度 = 右
#   50%  =    0 度 = 奥
#   100% =  -90 度 = 左
#
# 増える向きは VRChat のラジアルパペットのダイヤルと同じ向きに合わせてある。
# 逆にするとパペットを右へ倒したのに最下点が左へ行く（2026-08-09 に実測）。
#
# CamDroneOrbitGuideSetup.TiltDirMinDeg / TiltDirMaxDeg と必ず同じ値にすること。
# 片方だけ直すとガイドの目印と生成される軌道がずれる。
LOW_POINT_START_DEG = 90.0
LOW_POINT_SWEEP_DEG = 180.0

SLOT_COUNT = 5          # CamDroneOrbitGuideSetup.SlotCount

# 生成する JSON へそのまま書くカメラ設定の範囲。
# スロットに属さず、全体で1組しか持たない（一度に1本しかパスを作らないため）。
ZOOM_MIN = 20.0         # CamDroneOrbitGuideSetup.ZoomMin
ZOOM_MAX = 150.0        # CamDroneOrbitGuideSetup.ZoomMax
DURATION_MIN = 0.1      # CamDroneOrbitGuideSetup.DurationMin
DURATION_MAX = 60.0     # CamDroneOrbitGuideSetup.DurationMax
SPEED_MIN = 0.1         # CamDroneOrbitGuideSetup.SpeedMin
SPEED_MAX = 15.0        # CamDroneOrbitGuideSetup.SpeedMax
FOCAL_DISTANCE_MIN = 0.0    # CamDroneOrbitGuideSetup.FocalDistanceMin
FOCAL_DISTANCE_MAX = 10.0   # CamDroneOrbitGuideSetup.FocalDistanceMax
APERTURE_MIN = 1.4      # CamDroneOrbitGuideSetup.ApertureMin
APERTURE_MAX = 32.0     # CamDroneOrbitGuideSetup.ApertureMax

# スロットごとの書き出し先パス。固定で、メニューからは変えられない。
# VRChat の Multi ストリーミングは 4 本までしか扱えないため 0〜3 に収める。
# 5 番目は 1 番目と同じ 0 を使う。
PATH_BY_SLOT = {1: 0, 2: 1, 3: 2, 4: 3, 5: 0}

AXES = ("A", "B", "C")

# ---------------------------------------------------------------------------
# 校正で決まる定数
#
# 上の群と違い、アバター側には対応する値が無い。VRChat 側の性質を実測して
# 求めたもので、アバターを変えても同じ値が使える。
# ---------------------------------------------------------------------------

# レイが当たるのは VRChat のプレイヤーコライダーの表面。そこから中心までの
# オフセット(m)。アバターではなく VRChat 側の性質なので、config で調整できる。
DEFAULT_RHO = 0.2

# 1周あたりの点数 → 周回数。総点数が 50 を超えない範囲で 50 に最も近くなる値。
#
# 総点数を減らすと揺らぎの波（倍音 5 まで使う）のサンプリングが足りなくなり、
# なめらかなはずのうねりがギザギザに化ける。24 点を下回らせないこと。
DEFAULT_LAPS = {3: 16, 4: 12, 6: 8, 8: 6, 12: 4}

# 揺らぎに使う倍音。経路全体（全周回ぶん）を 1 周期とした波を重ねる。
#
# 仕様として固定で、config からは変えられない。総点数に対して大きすぎる倍音を
# 入れると点で山を表現しきれず、なめらかなはずの波がギザギザに化けるため。
# 目安は max(倍音) <= 総点数 ÷ 5。48 点なら 9 あたりが上限になる。
#
# 実測（半径 10m / Points 12 / 4周 / Random 20%、揺らぎなしの折れ角 30 度）:
#     (1, 2)           1周あたり 0.50 山   折れ角 最大 30.7 度
#     (2, 3, 5)        1周あたり 1.25 山   折れ角 最大 33.5 度  ← 採用
#     (3, 5, 8)        1周あたり 2.00 山   折れ角 最大 38.8 度
#     (5, 8, 13)       1周あたり 3.25 山   折れ角 最大 50.8 度  破綻
#
# 本数を増やすと山が打ち消し合って振れが弱まる。3本で指定値の約 78%。
#
# X と Z を独立に乱数で振ると、円周に沿う向きにも揺らぎが乗って点の間隔が乱れる。
# VRChat は点間を一定時間で補間するので、間隔のばらつきはそのままカメラ速度の
# ばらつきになる。半径10m・Points12・Random20% では間隔が 1.1〜10.2 m まで開き、
# 速度が最大 9 倍変わっていた。
#
# 半径方向だけを、隣り合う点が一緒に動く滑らかな波で揺らすとこれを避けられる。
# 倍音が整数なので経路の終端と始端がつながり、周回ごとに表情が変わる。
JITTER_HARMONICS = (2, 3, 5)

# 上下の揺らぎ。半径に対する比で与えるが、水平の 1/5 に抑える。
# 揺らぎ率に掛けるので、Random 10% で半径の 2%、20% で 4%。
#
# 水平と同じ比率（1.0）にはできない。半径は 1〜25 m と 25 倍の幅があり、
# 20% をそのまま上下へ入れると半径 25 m で ±5 m 振れて床下や頭上まで抜ける。
# Random% を手で下げても効くのは 2 倍ぶんだけで、25 倍の幅には足りない。
#
# 好みの範囲なので config で調整できる。0 にすると上下の揺らぎが無くなる。
DEFAULT_JITTER_VERTICAL_RATIO = 0.20

# 計算で決まらない値。添付サンプル（R48h20b15-2.json）の値をそのまま使う
DEFAULT_TEMPLATE: Dict[str, Any] = {
    "IsLocal": True,
    "FocalDistance": 1.5,
    "Aperture": 15.0,
    "Hue": 120.0,
    "Saturation": 100.0,
    "Lightness": 50.0,
    "LookAtMeXOffset": 0.0,
    "LookAtMeYOffset": 0.0,
    "Zoom": 45.0,
    "Exposure": 0.0,
    "Speed": 3.0,
    "Duration": 2.0,
    "PathIndex": 0,
}

# メニュー値が届いていないときの既定（アバター側の既定値と同じ）。実寸。
DEFAULT_MENU = {
    "Height": 1.2,
    "RingHeight": 1.2,
    "Radius": 2.0,
    "Tilt": 0.0,
    # 最下点の向き。正規化された 0〜1 のまま持つ。
    # 0.5 = 奥（CamDroneOrbitGuideSetup.TiltDirDefaultDeg = 0 度に対応）
    "TiltDir": 0.5,
    "Points": 6,
    # 揺らぎ(%)。0 / 10 / 20 のいずれかがアバターから届く
    "Random": 10,
    # 1 = 上から見て時計回り（右回り）、0 = 反時計回り（左回り）
    "CW": 1,
}

# カメラ設定が未受信のときの既定（アバター側の既定値と同じ）。実寸。
DEFAULT_CAMERA = {
    "Zoom": 45.0,           # CamDroneOrbitGuideSetup.ZoomDefault
    "FocalDistance": 1.5,   # CamDroneOrbitGuideSetup.FocalDistanceDefault
    "Aperture": 15.0,       # CamDroneOrbitGuideSetup.ApertureDefault
    "Duration": 2.0,        # CamDroneOrbitGuideSetup.DurationDefault
    "Speed": 3.0,           # CamDroneOrbitGuideSetup.SpeedDefault
}

# 正規化された 0〜1 を実寸へ戻すための範囲
CAMERA_RANGE = {
    "Zoom": (ZOOM_MIN, ZOOM_MAX),
    "FocalDistance": (FOCAL_DISTANCE_MIN, FOCAL_DISTANCE_MAX),
    "Aperture": (APERTURE_MIN, APERTURE_MAX),
    "Duration": (DURATION_MIN, DURATION_MAX),
    "Speed": (SPEED_MIN, SPEED_MAX),
}

# 起動時にバッファへ入れておく初期値。OSC で届くのと同じ正規化された形。
# 一度も操作されていない項目は送られてこないため、これで計算が常に成立する。
DEFAULT_MENU_NORMALIZED = {
    "Height": (DEFAULT_MENU["Height"] - HEIGHT_MIN) / (HEIGHT_MAX - HEIGHT_MIN),
    "RingHeight": (DEFAULT_MENU["RingHeight"] - HEIGHT_MIN) / (HEIGHT_MAX - HEIGHT_MIN),
    "Radius": (DEFAULT_MENU["Radius"] - RADIUS_MIN) / (RADIUS_MAX - RADIUS_MIN),
    "Tilt": (DEFAULT_MENU["Tilt"] - TILT_MIN) / (TILT_MAX - TILT_MIN),
    "TiltDir": DEFAULT_MENU["TiltDir"],
    "Points": float(DEFAULT_MENU["Points"]),
    "Random": float(DEFAULT_MENU["Random"]),
    "CW": float(DEFAULT_MENU["CW"]),
}

# Confirm 時に使う区間。押す動作で身体が動くので、押した瞬間ではなく少し前を見る
SAMPLE_WINDOW_START = 1.2   # 秒前から
SAMPLE_WINDOW_END = 0.3     # 秒前まで
# 測距値の保持件数。時間ではなく件数で切る。
# VRChat は値が変化したときにしか送らないので、静止すると補充が来ない。
# 時間で捨てると、じっとしているだけで印の位置を見失う。
# 届いた分だけ溜まるので、500 件あれば動き続けても数十秒ぶんになる。
SAMPLES_KEPT = 500

# 最後に受信してからこの秒数を過ぎたら「待機中」表示に戻す
ACTIVE_TIMEOUT = 5.0

PARAM_ROOT = "/avatar/parameters/"

# VRChat が受け付けるカメラドリーの操作。OSCQuery で公開されている実物。
#   /dolly/Import      s  ファイルパスを渡すとその場で読み込む
#   /dolly/Play        T  再生
#   /dolly/PlayDelayed i  指定秒後に再生
#
# 手でメニューを開いてパスを貼る操作が不要になる。座標は Import した瞬間の
# 位置と向きが基準なので、その間に体が動く余地を無くせるのが大きい。
DOLLY_IMPORT = "/dolly/Import"
DOLLY_PLAY = "/dolly/Play"
DOLLY_PLAY_DELAYED = "/dolly/PlayDelayed"

# VRChat は IsLocal のドリー座標を「水平方向だけ」目線の高さで正規化する。
# Y はメートルのまま解釈される。
#
#     JSON の X,Z = 実際のメートル × REFERENCE_EYE_HEIGHT / 実際の目線の高さ
#     JSON の Y   = 実際のメートル（無補正）
#
# 根拠: 半径 7.00 m / 高さ 0.90 m で生成したパスを VRChat に読み込ませ、
# ワールド座標に切り替えて書き出させたものと突き合わせた（2026-08-08）。
# 48点すべての対応から「平行移動 + 向き + 水平方向の一様倍率」を最小二乗で解くと
#
#   向き   121.085953 度（48点のばらつき 3e-05 度）
#   水平   world = local × 0.56893387      残差 最大 2e-06 m
#   Y      world = local - 0.005000 m      全48点で完全に一定
#
# Y に倍率は掛かっていない。残る 5 mm は床の高さで、倍率とは関係しない。
#
# 目線 0.935 m で割り戻すと旋回半径がちょうど 7.00000 m になるので、
# 目線の値と指定半径の両方がこの1件で裏付けられている。
#   reference_eye_height = 目線 0.935 / 0.56893387 = 1.643425
# 当初の見込み値 1.85 では水平方向が 12.6% 大きく出ていた。
#
# 目線の高さへの反比例は、アバタースケールを変えて確認済み（2026-08-08）。
#   目線 0.935 m -> k 0.56893388 -> 半径 7.00 指定に対し 7.000001（誤差 0.000%）
#   目線 1.897 m -> k 1.15436436 -> 半径 7.11 指定に対し 7.113841（誤差 0.054%）
# 0.935 で校正した値が、その 2.03 倍の目線でもそのまま通る。
# 2件目の 0.054% はファイル名が半径を小数2桁に丸めているぶん。
REFERENCE_EYE_HEIGHT = 1.643425
EYE_HEIGHT_ADDRESS = "/avatar/eyeheight"

# 出力先の既定。環境変数と ~ を展開して使うので、どのユーザー環境でもそのまま動く。
# VRChat の Camera Dolly が既定で参照する場所に合わせてある。
DEFAULT_OUTPUT_DIR = r"%USERPROFILE%\Documents\VRChat\CameraPaths"


# ---------------------------------------------------------------------------
# 設定
# ---------------------------------------------------------------------------

def resolve_path(raw: str) -> Tuple[Path, Optional[str]]:
    """環境変数と ~ を展開する。展開しきれなかった場合は理由を返す。

    config.json に特定のユーザー名を書かずに済むよう、
    %USERPROFILE% や ~ をそのまま書けるようにしている。
    """
    expanded = os.path.expandvars(os.path.expanduser(raw.strip()))
    if "%" in expanded or expanded.startswith("~"):
        fallback = APP_DIR / "output"
        return fallback, (f"output_dir を展開できませんでした（{raw}）。"
                          f"{fallback} を使用します")
    return Path(expanded), None


class Config:
    def __init__(self, data: Dict[str, Any]) -> None:
        self.receive_port = int(data.get("receive_port", 9001))
        self.send_port = int(data.get("send_port", 9000))
        self.output_dir, self.output_dir_note = resolve_path(
            str(data.get("output_dir", DEFAULT_OUTPUT_DIR)))
        self.debug = bool(data.get("debug", False))

        # 以下は任意。省略時は上の既定値を使う
        self.host = str(data.get("host", "127.0.0.1"))
        # 接続方法。oscquery なら mDNS で自分を広告し、VRChat に見つけてもらう。
        # osc なら receive_port を固定で使う従来どおりの方式。
        self.connection = str(data.get("connection", "oscquery")).strip().lower()
        if self.connection not in ("oscquery", "osc"):
            self.connection = "oscquery"
        self.fallback = bool(data.get("fallback", True))
        # 生成した直後に VRChat へ読み込ませる。手でパスを貼る必要が無くなり、
        # Confirm から Import までに体が動く余地も無くなる。
        self.auto_import = bool(data.get("auto_import", True))
        # 読み込み後の再生。負の値と未設定は再生しない。
        # 0 は即時、正の値はその秒数だけ待ってから再生する。
        raw_delay = data.get("auto_play_delay")
        self.auto_play_delay = int(raw_delay) if raw_delay is not None else None
        # Confirm を押した時点の値でも1本作る。読み込ませず出力するだけで、
        # 区間の中央値との差を確認するために使う。
        self.write_at_press = bool(data.get("write_at_press", True))
        self.rho = float(data.get("rho", DEFAULT_RHO))
        # 上下の揺らぎを水平の何倍にするか。0 で上下の揺らぎ無し。
        self.jitter_vertical_ratio = float(
            data.get("jitter_vertical_ratio", DEFAULT_JITTER_VERTICAL_RATIO))
        self.reference_eye_height = float(
            data.get("reference_eye_height", REFERENCE_EYE_HEIGHT))
        # 点数ごとの周回数は仕様として固定。総点数を減らすと揺らぎの波が
        # 粗くなるため、外から変えられないようにしてある。
        self.laps = dict(DEFAULT_LAPS)
        self.template = dict(DEFAULT_TEMPLATE)
        self.template.update(data.get("template", {}))

    @classmethod
    def load(cls) -> Tuple["Config", List[str]]:
        notes: List[str] = []
        if not CONFIG_PATH.exists():
            notes.append(f"config.json が見つかりません: {CONFIG_PATH}")
            notes.append("既定値で起動します。config.json を作成してください。")
            return cls({}), notes

        try:
            with CONFIG_PATH.open(encoding="utf-8") as handle:
                data = json.load(handle)
        except Exception as exc:
            notes.append(f"config.json の読み込みに失敗しました: {exc}")
            notes.append("既定値で起動します。")
            return cls({}), notes

        notes.append(f"config.json を読み込みました: {CONFIG_PATH}")
        return cls(data), notes


# ---------------------------------------------------------------------------
# ログ
# ---------------------------------------------------------------------------

class Logger:
    def __init__(self, debug: bool) -> None:
        self.debug_enabled = debug
        self.lock = threading.Lock()
        LOG_DIR.mkdir(parents=True, exist_ok=True)
        stamp = datetime.now().strftime("%Y%m%d%H%M%S")
        self.path = LOG_DIR / f"{APP_NAME}_{stamp}.log"
        self.handle = self.path.open("a", encoding="utf-8")

    def _write(self, level: str, message: str) -> None:
        line = f"[{datetime.now().strftime('%Y-%m-%d %H:%M:%S.%f')[:-3]}] [{level}] {message}\n"
        with self.lock:
            self.handle.write(line)
            self.handle.flush()

    def info(self, message: str) -> None:
        self._write("INFO", message)

    def warn(self, message: str) -> None:
        self._write("WARN", message)

    def error(self, message: str) -> None:
        self._write("ERROR", message)

    def debug(self, message: str) -> None:
        if self.debug_enabled:
            self._write("DEBUG", message)

    def close(self) -> None:
        with self.lock:
            try:
                self.handle.close()
            except Exception:
                pass


# ---------------------------------------------------------------------------
# 受信状態
# ---------------------------------------------------------------------------

class SlotState:
    """スロット1つ分の受信状態。測距値だけ時刻付きで溜める。"""

    def __init__(self) -> None:
        self.samples: Dict[str, Deque[Tuple[float, float]]] = {
            a: deque(maxlen=SAMPLES_KEPT) for a in AXES}
        self.hits: Dict[str, Deque[Tuple[float, bool]]] = {
            a: deque(maxlen=SAMPLES_KEPT) for a in AXES}
        # 起動時に初期値で埋めておく。一度も操作されていない項目は
        # VRChat から送られてこないため、これが無いと計算が成立しない。
        self.menu: Dict[str, float] = dict(DEFAULT_MENU_NORMALIZED)
        # 出どころの区別。受信したら外す
        self.initial: set = set(DEFAULT_MENU_NORMALIZED)
        self.confirm = False

    def push_distance(self, axis: str, value: float, now: float) -> None:
        self.samples[axis].append((now, value))

    def push_hit(self, axis: str, value: bool, now: float) -> None:
        self.hits[axis].append((now, value))

    def latest(self, axis: str) -> Optional[float]:
        """最後に受け取った値。Confirm を押した時点の状態にあたる。"""
        buffer = self.samples[axis]
        return buffer[-1][1] if buffer else None

    def latest_sample(self, axis: str) -> Optional[Tuple[float, float]]:
        """最後に受け取った値と、その時刻。"""
        buffer = self.samples[axis]
        return buffer[-1] if buffer else None

    def latest_hit(self, axis: str) -> Optional[bool]:
        buffer = self.hits[axis]
        return buffer[-1][1] if buffer else None

    def window_median(self, axis: str, now: float) -> Optional[float]:
        values = [v for (t, v) in self.samples[axis]
                  if now - SAMPLE_WINDOW_START <= t <= now - SAMPLE_WINDOW_END]
        if not values:
            # 区間に何も無ければ諦める。ここで全件の中央値へ落ちると、
            # 動いていた頃の古い値まで混ざった距離になる。
            # 呼び出し側が最後の1件で代用する。
            return None
        values.sort()
        return values[len(values) // 2]

    def window_all_hit(self, axis: str, now: float) -> Optional[bool]:
        values = [v for (t, v) in self.hits[axis]
                  if now - SAMPLE_WINDOW_START <= t <= now - SAMPLE_WINDOW_END]
        if not values:
            values = [v for (_, v) in self.hits[axis]]
        if not values:
            return None
        return all(values)

    def spread(self, axis: str, now: float) -> Optional[float]:
        values = [v for (t, v) in self.samples[axis]
                  if now - SAMPLE_WINDOW_START <= t <= now - SAMPLE_WINDOW_END]
        if len(values) < 2:
            return None
        return max(values) - min(values)

    def counts(self, axis: str, now: float) -> Tuple[int, int]:
        """(区間内の件数, バッファ全体の件数) を返す。区間が空なら直近値へ退避している。"""
        total = len(self.samples[axis])
        inside = sum(1 for (t, _) in self.samples[axis]
                     if now - SAMPLE_WINDOW_START <= t <= now - SAMPLE_WINDOW_END)
        return inside, total


class State:
    def __init__(self) -> None:
        self.lock = threading.Lock()
        self.slots = {n: SlotState() for n in range(1, SLOT_COUNT + 1)}
        self.last_message_at: Optional[float] = None
        self.total_messages = 0
        self.generated = 0
        # カメラ設定。スロットに属さないので State が持つ。正規化された 0〜1。
        self.camera: Dict[str, float] = {
            key: (DEFAULT_CAMERA[key] - lo) / (hi - lo)
            for key, (lo, hi) in CAMERA_RANGE.items()
        }
        # 一度も受信していない項目。ログで出どころを分けるために持つ
        self.camera_initial: set = set(CAMERA_RANGE)
        # VRChat が /avatar/eyeheight で送ってくる、現在のアバターの目線の高さ。
        # アバターの切り替え時とスケール変更時にしか届かない。
        self.eye_height: Optional[float] = None
        # OSC 送信クライアント。生成後の自動読み込みに使う
        self.client: Optional[Any] = None
        self.last_output: Optional[Path] = None
        # 水平倍率の微調整。トレイから変更して校正し、決まったら
        # config の reference_eye_height に書き戻す。
        self.scale_trim = 1.0

    def touch(self) -> None:
        self.last_message_at = time.monotonic()
        self.total_messages += 1

    def is_active(self) -> bool:
        if self.last_message_at is None:
            return False
        return time.monotonic() - self.last_message_at < ACTIVE_TIMEOUT



# ---------------------------------------------------------------------------
# 値の変換
# ---------------------------------------------------------------------------

def denorm(value: float, minimum: float, maximum: float) -> float:
    return minimum + value * (maximum - minimum)


def solve_center(ra: float, rb: float, rc: float, rho: float, baseline: float) -> Tuple[float, float]:
    """3本の距離から固定点のプレイヤー基準の相対位置 (x, z) を求める。

    原点は A=(0,0)、B=(baseline,0)、C=(0,baseline)。
    高さは差を取る過程で消えるので不要。
    """
    d = baseline
    x = (ra * ra - rb * rb + 2.0 * rho * (ra - rb) + d * d) / (2.0 * d)
    z = (ra * ra - rc * rc + 2.0 * rho * (ra - rc) + d * d) / (2.0 * d)
    # 解いたのは胴体の位置なので、固定点の位置は符号を反転したもの
    return -x, -z


def rotate_y(point: Tuple[float, float, float], degrees: float) -> Tuple[float, float, float]:
    a = math.radians(degrees)
    x, y, z = point
    return (x * math.cos(a) + z * math.sin(a), y, -x * math.sin(a) + z * math.cos(a))


def rotate_x(point: Tuple[float, float, float], degrees: float) -> Tuple[float, float, float]:
    a = math.radians(degrees)
    x, y, z = point
    return (x, y * math.cos(a) - z * math.sin(a), y * math.sin(a) + z * math.cos(a))


def look_rotation(camera: Tuple[float, float, float],
                  target: Tuple[float, float, float]) -> Tuple[float, float]:
    """カメラから注視点を見る回転を (X, Y) の度数で返す。

    サンプル JSON（R48h20b15-2.json）で実測値と一致することを確認済み。
    Unity は X が正で下向きなので、注視点が上にあるとき X は負になる。
    """
    dx = target[0] - camera[0]
    dy = target[1] - camera[1]
    dz = target[2] - camera[2]
    horizontal = math.hypot(dx, dz)
    yaw = math.degrees(math.atan2(dx, dz)) % 360.0
    pitch = -math.degrees(math.atan2(dy, horizontal)) if horizontal > 1e-9 else 0.0
    return pitch, yaw


# ---------------------------------------------------------------------------
# 軌道生成
# ---------------------------------------------------------------------------

class OrbitInput:
    def __init__(self) -> None:
        self.center_x = 0.0
        self.center_z = 0.0
        self.center_height = DEFAULT_MENU["Height"]
        self.ring_height = DEFAULT_MENU["RingHeight"]
        self.radius = DEFAULT_MENU["Radius"]
        self.tilt = DEFAULT_MENU["Tilt"]
        self.tilt_dir = DEFAULT_MENU["TiltDir"]
        self.points = int(DEFAULT_MENU["Points"])
        self.random_percent = int(DEFAULT_MENU["Random"])
        self.clockwise = bool(DEFAULT_MENU["CW"])
        self.report: List[str] = []   # ログにそのまま出す行
        self.defaulted: List[str] = []  # 既定値で埋めた項目
        # 中心が求まらなかったときに、欠けていた軸を入れる。
        # 空でなければ生成そのものを中止する。
        self.center_missing: List[str] = []


def tilt_mark_bearing(tilt_dir: float) -> float:
    """ガイドの目印が向く方位。傾きの符号によらずここを指す。

    アバター側の目印は TiltPivot の局所 +Z に置いてあるので、この方位が
    そのまま目印の位置になる。ログもこれを出さないとガイドと食い違う。
    """
    return LOW_POINT_START_DEG + tilt_dir * LOW_POINT_SWEEP_DEG


def tilt_mark_end(tilt: float) -> Optional[str]:
    """目印が円のどちら端かを返す。水平なら定まらないので None。

    局所 +Z が下がるのは傾きが正のときだけ。負のときは逆に上がる。
    どちらの端でも傾きは読み取れるので、位置は変えず呼び方だけ分ける。
    """
    if abs(tilt) < 1e-6:
        return None
    return "最下点" if tilt > 0.0 else "最高点"


def camera_values(state: "State") -> Tuple[Dict[str, float], List[str]]:
    """受信済みのカメラ設定を実寸へ戻す。報告用の行も一緒に返す。"""
    with state.lock:
        raw = dict(state.camera)
        initial = set(state.camera_initial)

    values: Dict[str, float] = {}
    report: List[str] = []
    for key, (lo, hi) in CAMERA_RANGE.items():
        values[key] = denorm(raw[key], lo, hi)
        tag = "[初期]" if key in initial else "[受信]"
        report.append(f"  {key:<12} パペット {raw[key]*100:5.1f}%  ->  "
                      f"{values[key]:8.2f}       {tag}")
    return values, report


def compass_name(degrees: float) -> str:
    """プレイヤー基準の方位を日本語の向きにする。0 が正面奥、+90 が右。"""
    names = ((0.0, "奥"), (45.0, "右奥"), (90.0, "右"), (135.0, "右手前"),
             (180.0, "手前"), (225.0, "左手前"), (270.0, "左"), (315.0, "左奥"))
    value = degrees % 360.0
    return min(names, key=lambda n: abs((value - n[0] + 180.0) % 360.0 - 180.0))[1]


def jitter_wave(progress: float, phases: Tuple[float, ...]) -> float:
    """経路の進み具合 0〜1 に対する -1〜+1 の滑らかな値を返す。

    倍音が整数なので progress=0 と 1 でつながる。位相は呼ぶ側が乱数で決める。
    倍音の数で割っているので、指定した揺らぎ幅を超えることはない。
    """
    angle = 2.0 * math.pi * progress
    total = sum(math.sin(h * angle + p) for h, p in zip(JITTER_HARMONICS, phases))
    return total / len(JITTER_HARMONICS)


def build_path(inputs: OrbitInput, laps: int, jitter: float,
               template: Dict[str, Any], scale: float = 1.0,
               vertical_ratio: float = DEFAULT_JITTER_VERTICAL_RATIO
               ) -> List[Dict[str, Any]]:
    """円周上に等分配置した点へ揺らぎを与え、傾きを適用して並べる。

    揺らぎは半径方向と上下方向にだけ与える。円周に沿う向きへは動かさないので
    点の間隔が保たれ、カメラの速度が一定に近くなる（JITTER_HARMONICS 参照）。

    scale は目線の高さによる正規化の倍率。VRChat が正規化するのは水平方向
    だけなので、掛けるのは X と Z のみ。Y はメートルのまま書く。
    向きはメートル基準の座標から求めるので、倍率の影響を受けない。
    """
    center = (inputs.center_x, inputs.center_height, inputs.center_z)
    ring_origin = (inputs.center_x, inputs.ring_height, inputs.center_z)

    # 最下点の方位。0% が右、50% が手前、100% が左（LOW_POINT_START_DEG 参照）
    azimuth = LOW_POINT_START_DEG + inputs.tilt_dir * LOW_POINT_SWEEP_DEG

    # Unity は左手系なので、上から見ると θ を増やす向きは反時計回り（左回り）。
    # 時計回り（右回り）にするには θ の符号を反転する。
    spin = -1.0 if inputs.clockwise else 1.0

    # 半径方向と上下方向で別の位相を引く。生成のたびに違う軌道になる。
    radial_phases = tuple(random.uniform(0.0, 2.0 * math.pi) for _ in JITTER_HARMONICS)
    vertical_phases = tuple(random.uniform(0.0, 2.0 * math.pi) for _ in JITTER_HARMONICS)
    total_points = laps * inputs.points

    entries: List[Dict[str, Any]] = []
    index = 0
    for _lap in range(laps):
        for step in range(inputs.points):
            theta = spin * 2.0 * math.pi * step / inputs.points
            progress = index / total_points

            # 半径だけを伸び縮みさせる。角度は等分のまま動かさない
            radius = inputs.radius * (1.0 + jitter * jitter_wave(progress, radial_phases))
            local_x = radius * math.cos(theta)
            local_z = radius * math.sin(theta)

            # 上下は水平よりずっと浅く。傾きの前に混ぜると円ごと歪むので後で足す
            rise = (jitter * vertical_ratio * inputs.radius
                    * jitter_wave(progress, vertical_phases))

            # Unity の階層 TiltAzimuth(Y) -> TiltPivot(X) と同じ順序で適用する
            offset = rotate_y(rotate_x((local_x, 0.0, local_z), inputs.tilt), azimuth)

            position = (ring_origin[0] + offset[0],
                        ring_origin[1] + offset[1] + rise,
                        ring_origin[2] + offset[2])
            pitch, yaw = look_rotation(position, center)

            entry = dict(template)
            entry["Position"] = {"X": round(position[0] * scale, 6),
                                 "Y": round(position[1], 6),
                                 "Z": round(position[2] * scale, 6)}
            entry["Rotation"] = {"X": round(pitch, 6), "Y": round(yaw, 6), "Z": 0.0}
            entry["Index"] = index
            entries.append(entry)
            index += 1

    return entries


def collect_inputs(slot: SlotState, now: float, config: Config, log: Logger,
                   use_window: bool = True) -> OrbitInput:
    """受信済みの値を集める。足りないものは既定値で埋め、その旨を報告に残す。

    use_window=False にすると、区間の中央値ではなく最後に受け取った値を使う。
    Confirm を押した時点の状態にあたり、押す動作で身体が動いた分が入る。
    """
    inputs = OrbitInput()

    inputs.report.append("[受信状況]")

    distances: Dict[str, Optional[float]] = {}
    for axis in AXES:
        distances[axis] = (slot.window_median(axis, now) if use_window
                           else slot.latest(axis))
        hit = slot.window_all_hit(axis, now)
        inside, total = slot.counts(axis, now)
        spread = slot.spread(axis, now)

        stale_age: Optional[float] = None
        if distances[axis] is None:
            recent = slot.latest_sample(axis)
            if recent is not None:
                stamp, value = recent
                distances[axis] = value
                stale_age = now - stamp
                hit = slot.latest_hit(axis)

        if distances[axis] is None:
            inputs.report.append(f"  Probe{axis}      未受信")
            continue

        if stale_age is not None:
            inputs.report.append(
                f"  Probe{axis}      距離 {distances[axis]:.4f} m  Hit {hit}  "
                f"※ {stale_age:.0f} 秒前の最終値（動きが無いため補充されていない）")
            continue

        marks = []
        if hit is False:
            marks.append("当たっていない区間あり")
        if spread is not None and spread > 0.05:
            marks.append(f"ばらつき {spread*1000:.0f}mm（静止していない可能性）")

        inputs.report.append(
            f"  Probe{axis}      距離 {distances[axis]:.4f} m  Hit {hit}  "
            f"区間 {inside}件 / 全 {total}件"
            + ("  ※ " + " / ".join(marks) if marks else ""))

    if all(distances[a] is not None for a in AXES):
        inputs.center_x, inputs.center_z = solve_center(
            distances["A"], distances["B"], distances["C"], config.rho, PROBE_BASELINE)
    else:
        # 中心が求まらない。(0,0) は「自分の足元」であって推定値ではないので、
        # これで生成すると意味のないパスを VRChat へ読み込ませることになる。
        missing = [a for a in AXES if distances[a] is None]
        inputs.center_missing = missing
        inputs.report.append(f"  ※ 測距値({','.join(missing)})が無いため中心を求められません")

    menu = slot.menu

    def source(key: str) -> str:
        if key in slot.initial:
            inputs.defaulted.append(key)
            return "[初期]"
        return "[受信]"

    def take(key: str, minimum: float, maximum: float, default_value: float,
             unit: str) -> float:
        # パペットの表示は % なので、突き合わせやすいよう % も併記する
        if key in menu:
            raw = menu[key]
            value = denorm(raw, minimum, maximum)
            inputs.report.append(f"  {key:<12} パペット {raw*100:5.1f}%  ->  "
                                 f"{value:8.2f} {unit}   {source(key)}")
            return value
        inputs.defaulted.append(key)
        inputs.report.append(f"  {key:<12} パペット {'--':>5}   ->  "
                             f"{default_value:8.2f} {unit}   [既定]")
        return default_value

    inputs.center_height = take("Height", HEIGHT_MIN, HEIGHT_MAX, DEFAULT_MENU["Height"], "m")
    inputs.ring_height = take("RingHeight", HEIGHT_MIN, HEIGHT_MAX, DEFAULT_MENU["RingHeight"], "m")
    inputs.radius = take("Radius", RADIUS_MIN, RADIUS_MAX, DEFAULT_MENU["Radius"], "m")
    inputs.tilt = take("Tilt", TILT_MIN, TILT_MAX, DEFAULT_MENU["Tilt"], "度")

    # メニュー上の名前は Low Point。OSC のアドレスは TiltDir のまま
    if "TiltDir" in menu:
        inputs.tilt_dir = menu["TiltDir"]
        tag = source("TiltDir")
    else:
        inputs.tilt_dir = DEFAULT_MENU["TiltDir"]
        inputs.defaulted.append("TiltDir")
        tag = "[既定]"
    mark = tilt_mark_bearing(inputs.tilt_dir)
    end = tilt_mark_end(inputs.tilt)
    where = (f"{mark:+8.2f} 度 {compass_name(mark):<4}（{end}）" if end
             else f"{mark:+8.2f} 度 {compass_name(mark):<4}（傾き 0 で無効）")
    inputs.report.append(f"  {'LowPoint':<12} パペット {inputs.tilt_dir*100:5.1f}%  ->  "
                         f"{where} {tag}")

    if "Points" in menu:
        inputs.points = int(round(menu["Points"]))
        inputs.report.append(f"  {'Points':<12} {inputs.points:8d}                     {source('Points')}")
    else:
        inputs.defaulted.append("Points")
        inputs.report.append(f"  {'Points':<12} {int(DEFAULT_MENU['Points']):8d}                     [既定]")

    if inputs.points not in config.laps:
        inputs.report.append(f"  ※ Points={inputs.points} は想定外の値です。6 として扱います")
        inputs.points = 6

    if "Random" in menu:
        inputs.random_percent = int(round(menu["Random"]))
        inputs.report.append(f"  {'Random':<12} {inputs.random_percent:8d} %"
                             f"                   {source('Random')}")
    else:
        inputs.defaulted.append("Random")
        inputs.report.append(f"  {'Random':<12} {int(DEFAULT_MENU['Random']):8d} %"
                             f"                   [既定]")

    if "CW" in menu:
        inputs.clockwise = menu["CW"] >= 0.5
        label = "右回り" if inputs.clockwise else "左回り"
        inputs.report.append(f"  {'CW':<12} {label:>8}"
                             f"                     {source('CW')}")
    else:
        inputs.defaulted.append("CW")
        label = "右回り" if DEFAULT_MENU["CW"] else "左回り"
        inputs.report.append(f"  {'CW':<12} {label:>8}                     [既定]")

    return inputs


# 校正用に OSC で送る高さの候補(m)。パペットでは正確な値を作れないため。
HEIGHT_PRESETS = (0.0, 0.5, 1.0, 1.2, 2.0)


def send_height(client: Any, slot_number: int, meters: float,
                config: Config, log: Logger) -> None:
    """指定スロットの注視点と円の高さを、正確な値に設定する。

    VRChat は OSC の入力も受け付けるので、パペットを介さずに
    パラメータを直接書ける。正規化はツール側で行う。
    """
    if client is None:
        log.error("OSC 送信クライアントが利用できません")
        return

    value = (meters - HEIGHT_MIN) / (HEIGHT_MAX - HEIGHT_MIN)
    value = min(max(value, 0.0), 1.0)

    for key in ("Height", "RingHeight"):
        address = f"{PARAM_ROOT}CamDrone/Obj{slot_number}/{key}"
        try:
            client.send_message(address, float(value))
        except Exception as exc:
            log.error(f"送信に失敗しました {address}: {exc}")
            return

    log.info(f"Obj{slot_number}: 高さを {meters:.2f} m に設定しました"
             f"（正規化 {value:.4f} を {config.host}:{config.send_port} へ送信）")


def advertised_paths() -> List[str]:
    """OSCQuery で「これを送ってほしい」と宣言するアドレス一覧。"""
    paths = [oscquery.PARAM_ROOT.rstrip("/")]
    for slot in range(1, SLOT_COUNT + 1):
        for axis in AXES:
            base = f"{PARAM_ROOT}CamDrone/Probe{slot}_{axis}"
            paths += [base + "_Hit", base + "_Distance"]
        for key in ("Height", "RingHeight", "Radius", "Tilt", "TiltDir",
                    "Points", "Random", "CW", "Confirm"):
            paths.append(f"{PARAM_ROOT}CamDrone/Obj{slot}/{key}")

    for key in CAMERA_RANGE:
        paths.append(f"{PARAM_ROOT}CamDrone/Camera/{key}")
    paths.append(EYE_HEIGHT_ADDRESS)
    return paths


def bind_server(config: Config, port: int, disp: Any) -> Tuple[Any, int]:
    """UDP で待ち受ける。port が 0 なら OS が空きを選ぶ。

    実際に確保できた番号を返す。0 を渡したときは呼び出し側が知る術がない。
    """
    server = osc_server.ThreadingOSCUDPServer((config.host, port), disp)
    return server, server.server_address[1]


def establish_transport(config: Config, log: Logger,
                        disp: Any) -> Tuple[str, Optional[Any], Optional[Any], int]:
    """設定された優先順で接続方法を確立し、待ち受けまで済ませる。

    待ち受けるポートは方式によって別にする。

    - **oscquery** — 番号は mDNS で VRChat へ伝えるので、設定値である必要が
      無い。空きポートを取る。UDP リピーターのような「特定の番号へ配る」
      仕組みと番号が重ならないため、同じ配信を二重に受け取らずに済む。
    - **osc** — 番号を伝える手段が無いので、設定値をそのまま使う。

    両者を同じ番号にすると、リピーター経由の配信と VRChat からの直接送信が
    重なる。Confirm は立ち上がりで検出しているが、2系統がずれて届くと
    立ち上がりが2回成立し、1回の押下で2本生成されてしまう。
    """
    order = [config.connection]
    if config.fallback:
        order.append("osc" if config.connection == "oscquery" else "oscquery")

    for method in order:
        if method == "osc":
            try:
                server, port = bind_server(config, config.receive_port, disp)
            except OSError as exc:
                log.error(f"固定ポート {config.receive_port} で待ち受けられません: {exc}")
                continue
            log.info(f"接続方法: OSC(UDP) 固定ポート {port}")
            log.info("  VRChat 側の OSC 送信先をこのポートに合わせてください")
            return "osc", None, server, port

        if not oscquery.AVAILABLE:
            log.warn("OSCQuery を使えません（zeroconf が見つかりません）")
            continue

        try:
            server, port = bind_server(config, 0, disp)
        except OSError as exc:
            log.error(f"待ち受けポートを確保できません: {exc}")
            continue

        service = oscquery.Service(oscquery.SERVICE_NAME, port)
        ok, note = service.start(advertised_paths())
        if ok:
            log.info(f"接続方法: OSCQuery — {note}")
            log.info("  VRChat が自動で見つけるため、送信先の設定は不要です")
            return "oscquery", service, server, port

        log.warn(note)
        server.server_close()  # 次の方法で取り直す

    log.error("どの方法でも待ち受けを開始できませんでした")
    return "osc", None, None, config.receive_port


def ingest_snapshot(values: Dict[str, Any], state: State, log: Logger) -> Tuple[int, int]:
    """OSCQuery で取得した現在値をバッファへ流し込む。

    メニュー値（CamDrone/Obj{N}/{key}）と測距値（CamDrone/Probe{N}_{axis}_{kind}）の
    両方を取り込む。素の OSC は値が変化したときしか届かないため、
    ツール起動前から当たり続けているレイの Hit は一度も来ない。
    ここで取り込まないと「当たっているか分からない」状態のまま計算してしまう。

    Confirm は押しボタンなので取り込まない。誤って生成が走るのを防ぐ。
    """
    menu_applied = probe_applied = 0
    now = time.monotonic()
    with state.lock:
        for address, value in values.items():
            name = address[len(PARAM_ROOT):]

            if name.startswith("CamDrone/Probe"):
                parts = name[len("CamDrone/Probe"):].split("_")
                if len(parts) != 3:
                    continue
                try:
                    slot_number = int(parts[0])
                except ValueError:
                    continue
                axis, kind = parts[1], parts[2]
                if slot_number not in state.slots or axis not in AXES:
                    continue
                slot = state.slots[slot_number]
                if kind == "Distance":
                    try:
                        slot.push_distance(axis, float(value), now)
                    except (TypeError, ValueError):
                        continue
                elif kind == "Hit":
                    slot.push_hit(axis, bool(value), now)
                else:
                    continue
                probe_applied += 1
                continue

            if name.startswith("CamDrone/Camera/"):
                key = name[len("CamDrone/Camera/"):]
                if key not in CAMERA_RANGE:
                    continue
                try:
                    state.camera[key] = float(value)
                except (TypeError, ValueError):
                    continue
                state.camera_initial.discard(key)
                menu_applied += 1
                continue

            if not name.startswith("CamDrone/Obj"):
                continue
            parts = name.split("/")
            if len(parts) != 3 or parts[2] == "Confirm":
                continue
            try:
                slot_number = int(parts[1][len("Obj"):])
                numeric = float(value)
            except (TypeError, ValueError):
                continue
            if slot_number not in state.slots:
                continue
            slot = state.slots[slot_number]
            slot.menu[parts[2]] = numeric
            slot.initial.discard(parts[2])
            menu_applied += 1
    return menu_applied, probe_applied


def refresh_from_oscquery(state: State, config: Config, log: Logger) -> None:
    """VRChat に現在値を問い合わせて取り込む。

    素の OSC は値が変化したときにしか届かないため、起動直後は
    「触っていない項目が分からない」という穴がある。ここを埋める。
    """
    log.info("=" * 68)
    log.info("VRChat へ現在値を問い合わせます")

    if not oscquery.AVAILABLE:
        log.warn("zeroconf が無いため問い合わせできません")
        log.info("=" * 68)
        return

    values = oscquery.fetch_from_any(log=log.info, skip_name=oscquery.SERVICE_NAME)
    if not values:
        log.warn("現在値を取得できませんでした。アバターの読み込み直しで代用してください")
        log.info("=" * 68)
        return

    menu_applied, probe_applied = ingest_snapshot(values, state, log)
    log.info(f"メニュー値 {menu_applied} 件 / 測距値 {probe_applied} 件を取り込みました")

    # 目線の高さは /avatar/parameters の外にある。別名でも来るので両方見る。
    eye = values.get(EYE_HEIGHT_ADDRESS)
    if not isinstance(eye, (int, float)) or eye <= 0.01:
        eye = values.get(PARAM_ROOT + "EyeHeightAsMeters")

    if isinstance(eye, (int, float)) and eye > 0.01:
        with state.lock:
            state.eye_height = float(eye)
        log.info(f"目線の高さ: {eye:.3f} m "
                 f"(水平倍率 {config.reference_eye_height / eye:.4f})")
    else:
        log.warn("目線の高さを取得できませんでした。水平倍率は無補正のままです")

    log.info("=" * 68)


def send_dolly_import(state: State, config: Config, log: Logger,
                      path: Path) -> None:
    """生成したパスを VRChat へ読み込ませる。

    座標は Import した瞬間のプレイヤーの位置と向きが基準になる。
    手でメニューを開いていると、その間の移動や向きの変化がそのまま
    ずれになるため、生成直後にここで読み込ませてしまう。
    """
    with state.lock:
        client = state.client

    if client is None:
        log.warn("  OSC 送信クライアントが無いため自動読み込みできません")
        return

    try:
        client.send_message(DOLLY_IMPORT, str(path))
    except Exception as exc:
        log.error(f"  自動読み込みに失敗しました: {exc}")
        return

    log.info(f"  自動読み込み  = {DOLLY_IMPORT} へ送信しました")

    # 負の値と未設定は再生しない。0 は即時、正の値はその秒数だけ待つ。
    delay = config.auto_play_delay
    if delay is None or delay < 0:
        return

    try:
        if delay == 0:
            client.send_message(DOLLY_PLAY, True)
            log.info("  自動再生      = 即時")
        else:
            client.send_message(DOLLY_PLAY_DELAYED, int(delay))
            log.info(f"  自動再生      = {delay} 秒後")
    except Exception as exc:
        log.warn(f"  自動再生の指示に失敗しました: {exc}")


def resolve_position_scale(state: State, config: Config) -> Tuple[float, str]:
    """JSON に書く水平座標（X,Z）へ掛ける倍率を決める。

    VRChat は IsLocal の水平座標をプレイヤーの目線の高さで正規化した空間で
    解釈するため、メートル値をそのまま書くとアバターの大きさに応じて縮む。
    Y は正規化されないので、この倍率は掛けない。
    """
    with state.lock:
        eye = state.eye_height
        trim = state.scale_trim

    if eye is None or eye <= 0.01:
        return 1.0, ("  水平倍率      = 1.0000（目線の高さが一度も届いていないため無補正。"
                     "アバターを読み込み直すと届きます）")

    scale = config.reference_eye_height / eye * trim
    note = (f"  水平倍率      = {scale:.4f}"
            f"（目線 {eye:.3f} m [受信] / 基準 {config.reference_eye_height:.4f} m")
    if abs(trim - 1.0) > 1e-6:
        note += f" × 微調整 {trim:.3f}"
        note += (f"）\n                  ※確定したら REFERENCE_EYE_HEIGHT を "
                 f"{config.reference_eye_height * trim:.4f} にしてください")
    else:
        note += "）"
    return scale, note


def dump_all_slots(state: State, config: Config, log: Logger) -> None:
    """全スロットの受信状況をログに出す。JSON は書き出さない。

    校正用。各点で「円周が自分に重なるまで Radius を合わせる」と、
    その Radius がマーカーまでの真の距離になる。測定値と並べれば
    物差し無しで測定側の誤差の形が分かる。
    """
    now = time.monotonic()
    log.info("=" * 78)
    log.info("全スロットの受信状況")
    log.info("  Radius は「円周を自分に重ねた」状態なら真の距離とみなせる")
    log.info("  " + "-" * 74)
    log.info("        rA      rB      rC     測定距離   Radius(真値)   測定/真値")

    with state.lock:
        for slot_number in range(1, SLOT_COUNT + 1):
            slot = state.slots[slot_number]

            values = {a: slot.window_median(a, now) or slot.latest(a) for a in AXES}
            if any(values[a] is None for a in AXES):
                got = [a for a in AXES if values[a] is not None]
                log.info(f"  Obj{slot_number}: 未受信"
                         + (f"（{','.join(got)} のみ受信）" if got else ""))
                continue

            x, z = solve_center(values["A"], values["B"], values["C"],
                                config.rho, PROBE_BASELINE)
            measured = math.hypot(x, z)

            if "Radius" in slot.menu:
                truth = denorm(slot.menu["Radius"], RADIUS_MIN, RADIUS_MAX)
                truth_text = f"{truth:8.2f} m"
                ratio_text = f"{measured / truth:8.3f}" if truth > 1e-6 else "       -"
            else:
                truth_text = "   未受信"
                ratio_text = "       -"

            log.info(f"  Obj{slot_number} {values['A']:7.3f} {values['B']:7.3f} "
                     f"{values['C']:7.3f}  {measured:8.3f} m  {truth_text}  {ratio_text}")

    log.info("  " + "-" * 74)
    log.info(f"  （基線 {PROBE_BASELINE} m / ρ {config.rho} m で計算）")
    log.info("=" * 78)


def output_name(inputs: OrbitInput, prefix: str = "") -> str:
    stamp = datetime.now().strftime("%Y%m%d%H%M%S")
    return (f"{prefix}R{inputs.radius:.2f}P{inputs.points}"
            f"H{inputs.center_height:.2f}_{stamp}.json")


def generate(slot_number: int, state: State, config: Config, log: Logger,
             jitter: Optional[float] = None, prefix: str = "",
             trigger: str = "Confirm を受信しました") -> Optional[Path]:
    """1回分の生成処理。受信状況・使った値・結果を1ブロックにまとめて記録する。

    jitter を指定すると config の値ではなくその値を使う。
    0 を渡せば揺らぎ無しになり、同じ入力から毎回同じ結果が出る。
    """
    now = time.monotonic()

    log.info("=" * 68)
    log.info(f"Obj{slot_number}: {trigger}")

    with state.lock:
        slot = state.slots[slot_number]
        inputs = collect_inputs(slot, now, config, log)
        at_press = (collect_inputs(slot, now, config, log, use_window=False)
                    if config.write_at_press else None)

    for line in inputs.report:
        log.info(line)

    # 中心が求まらないまま進むと、自分の足元を中心にした無意味なパスを
    # VRChat へ読み込ませてしまう。ここで止める。
    if inputs.center_missing:
        log.warn(f"測距値({','.join(inputs.center_missing)})が届いていないため中止しました。"
                 "生成も読み込みもしていません")
        log.warn("  レイが固定点に当たっているか、アバターを読み込み直して"
                 "現在値が届くかを確認してください")
        log.info("=" * 24 + " 中止 " + "=" * 24)
        return None

    if inputs.defaulted:
        log.info("初期値のまま使った項目: " + ", ".join(inputs.defaulted)
                 + "（一度も操作していなければ正常。意図と違う場合は"
                   "ツール起動後にアバターを読み込み直すと現在値が届きます）")

    # 揺らぎはアバターのメニューで選んだ値を使う。未受信でも
    # DEFAULT_MENU["Random"] で埋まっているので必ず決まる。
    # jitter 引数（揺らぎなしで出力するメニュー）だけがこれに優先する。
    effective_jitter = jitter if jitter is not None else inputs.random_percent / 100.0

    scale, scale_note = resolve_position_scale(state, config)
    log.info(scale_note)

    # カメラ設定はスロットに属さないので State から取る
    camera, camera_report = camera_values(state)
    for line in camera_report:
        log.info(line)
    template = dict(config.template)
    template.update(camera)
    # 書き出し先のパスはスロットで決まる。同時に1本しか作らないので固定でよい
    template["PathIndex"] = PATH_BY_SLOT.get(slot_number, 0)

    try:
        laps = config.laps.get(inputs.points, 8)
        entries = build_path(inputs, laps, effective_jitter, template, scale,
                             config.jitter_vertical_ratio)
    except Exception as exc:
        log.error(f"軌道の計算に失敗しました: {exc}")
        log.info("=" * 24 + " 失敗 " + "=" * 24)
        return None

    mark = tilt_mark_bearing(inputs.tilt_dir)
    mark_end = tilt_mark_end(inputs.tilt)
    log.info("[計算結果]")
    log.info(f"  中心 (x, z)   = ({inputs.center_x:+.3f}, {inputs.center_z:+.3f})")
    log.info(f"  注視点        = ({inputs.center_x:+.3f}, {inputs.center_height:.2f}, "
             f"{inputs.center_z:+.3f})")
    log.info(f"  円の高さ      = {inputs.ring_height:.2f} m")
    log.info(f"  半径          = {inputs.radius:.2f} m")
    if mark_end is None:
        log.info(f"  傾き / 目印   = {inputs.tilt:+.1f} 度 / 円が水平なので傾きなし")
    else:
        log.info(f"  傾き / 目印   = {inputs.tilt:+.1f} 度 / {mark:+.1f} 度 "
                 f"{compass_name(mark)} = {mark_end}（プレイヤー基準、0 が正面奥）")
    log.info(f"  レンズ        = Zoom {camera['Zoom']:.1f} / "
             f"FocalDistance {camera['FocalDistance']:.2f} / "
             f"Aperture {camera['Aperture']:.2f}")
    log.info(f"  動き          = Duration {camera['Duration']:.2f} 秒 / "
             f"Speed {camera['Speed']:.2f}")
    log.info(f"  書き出し先    = Path {template['PathIndex']}"
             f"（Pivot {slot_number} 固定）")
    log.info(f"  点数          = {inputs.points} × {laps}周 = {len(entries)} 点")
    log.info(f"  周回の向き    = {'右回り' if inputs.clockwise else '左回り'}（上から見て）")
    if effective_jitter > 0.0:
        log.info(f"  揺らぎ        = ±{effective_jitter*100:.0f}%"
                 f"（半径 ±{effective_jitter*inputs.radius:.2f} m / "
                 f"上下 ±{effective_jitter*config.jitter_vertical_ratio*inputs.radius:.2f} m）")
    else:
        log.info("  揺らぎ        = なし")

    name = output_name(inputs, prefix)
    body = json.dumps(entries, indent=2, ensure_ascii=False)

    try:
        config.output_dir.mkdir(parents=True, exist_ok=True)
        path = config.output_dir / name
        path.write_text(body, encoding="utf-8")
    except Exception as exc:
        log.error(f"書き出しに失敗しました: {exc}")
        log.info("=" * 24 + " 失敗 " + "=" * 24)
        return None

    log.info(f"  書き出し      = {path}")

    # 控えは失敗しても本体の成否には影響させない
    try:
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        (DATA_DIR / name).write_text(body, encoding="utf-8")
        log.info(f"  控え          = {DATA_DIR / name}")
    except Exception as exc:
        log.warn(f"  控えの書き出しに失敗しました: {exc}")

    with state.lock:
        state.last_output = path

    if config.auto_import:
        send_dolly_import(state, config, log, path)

    # Confirm を押した時点の値でもう1本。読み込ませず出力するだけ。
    # 押す動作でどれだけ動いたかを、あとから見比べるために残す。
    if at_press is not None:
        write_at_press_copy(at_press, inputs, laps, effective_jitter, scale, template,
                            config, log)

    log.info("=" * 24 + " 成功 " + "=" * 24)
    return path


def write_at_press_copy(at_press: OrbitInput, reference: OrbitInput, laps: int,
                        jitter: float, scale: float, template: Dict[str, Any],
                        config: Config, log: Logger) -> None:
    """Confirm を押した時点の値で作ったパスを、出力先へ書くだけ。"""
    dx = at_press.center_x - reference.center_x
    dz = at_press.center_z - reference.center_z
    gap = math.hypot(dx, dz)

    log.info("[Confirm 時点との比較]")
    log.info(f"  区間の中央値  = ({reference.center_x:+.3f}, {reference.center_z:+.3f})"
             f"  距離 {math.hypot(reference.center_x, reference.center_z):.3f} m")
    log.info(f"  押した時点    = ({at_press.center_x:+.3f}, {at_press.center_z:+.3f})"
             f"  距離 {math.hypot(at_press.center_x, at_press.center_z):.3f} m")
    log.info(f"  差            = {gap:.3f} m")

    try:
        entries = build_path(at_press, laps, jitter, template, scale,
                             config.jitter_vertical_ratio)
        name = output_name(at_press, "ATPRESS_")
        body = json.dumps(entries, indent=2, ensure_ascii=False)
        (config.output_dir / name).write_text(body, encoding="utf-8")
        DATA_DIR.mkdir(parents=True, exist_ok=True)
        (DATA_DIR / name).write_text(body, encoding="utf-8")
        log.info(f"  比較用        = {config.output_dir / name}（読み込ませません）")
    except Exception as exc:
        log.warn(f"  比較用の書き出しに失敗しました: {exc}")


# ---------------------------------------------------------------------------
# OSC
# ---------------------------------------------------------------------------

def build_dispatcher(state: State, config: Config, log: Logger) -> osc_dispatcher.Dispatcher:
    disp = osc_dispatcher.Dispatcher()

    def handle_any(address: str, *args: Any) -> None:
        now = time.monotonic()
        with state.lock:
            state.touch()

        if config.debug:
            log.debug(f"{address} {args}")

        if address == EYE_HEIGHT_ADDRESS and args:
            try:
                value = float(args[0])
            except (TypeError, ValueError):
                return
            if value > 0.01:
                with state.lock:
                    changed = state.eye_height != value
                    state.eye_height = value
                if changed:
                    log.info(f"目線の高さ: {value:.3f} m "
                             f"(水平倍率 {config.reference_eye_height / value:.4f})")
            return

        if address == "/avatar/change":
            # アバターのロード時。この直後に VRChat が全パラメータを一度送ってくる
            log.info("アバターの切り替えを検出しました。現在値が届くはずです")
            return

        if not address.startswith(PARAM_ROOT) or not args:
            return

        name = address[len(PARAM_ROOT):]
        value = args[0]

        if name.startswith("CamDrone/Probe"):
            handle_probe(name, value, now, state, log)
        elif name.startswith("CamDrone/Obj"):
            handle_menu(name, value, state, config, log)
        elif name.startswith("CamDrone/Camera/"):
            handle_camera(name, value, state)

    def handle_camera(name: str, value: Any, st: State) -> None:
        # CamDrone/Camera/{Zoom|Duration|Speed}。Reset* はアバター側で完結する
        key = name[len("CamDrone/Camera/"):]
        if key not in CAMERA_RANGE:
            return
        try:
            numeric = float(value)
        except (TypeError, ValueError):
            return
        with st.lock:
            st.camera[key] = numeric
            st.camera_initial.discard(key)

    def handle_probe(name: str, value: Any, now: float, st: State, lg: Logger) -> None:
        # CamDrone/Probe{N}_{axis}_{Hit|Ratio|Distance}
        body = name[len("CamDrone/Probe"):]
        parts = body.split("_")
        if len(parts) != 3:
            return
        try:
            slot_number = int(parts[0])
        except ValueError:
            return
        axis, kind = parts[1], parts[2]
        if slot_number not in st.slots or axis not in AXES:
            return

        with st.lock:
            slot = st.slots[slot_number]
            if kind == "Distance":
                try:
                    slot.push_distance(axis, float(value), now)
                except (TypeError, ValueError):
                    pass
            elif kind == "Hit":
                slot.push_hit(axis, bool(value), now)

    def handle_menu(name: str, value: Any, st: State, cfg: Config, lg: Logger) -> None:
        # CamDrone/Obj{N}/{key}
        parts = name.split("/")
        if len(parts) != 3:
            return
        try:
            slot_number = int(parts[1][len("Obj"):])
        except ValueError:
            return
        key = parts[2]
        if slot_number not in st.slots:
            return

        if key == "Confirm":
            pressed = bool(value)
            with st.lock:
                slot = st.slots[slot_number]
                rising = pressed and not slot.confirm
                slot.confirm = pressed
            if rising:
                threading.Thread(target=generate,
                                 args=(slot_number, st, cfg, lg),
                                 daemon=True).start()
                with st.lock:
                    st.generated += 1
            return

        try:
            numeric = float(value)
        except (TypeError, ValueError):
            return

        with st.lock:
            slot = st.slots[slot_number]
            changed = slot.menu.get(key) != numeric or key in slot.initial
            slot.menu[key] = numeric
            slot.initial.discard(key)

    disp.set_default_handler(handle_any)
    return disp


# ---------------------------------------------------------------------------
# タスクトレイ
# ---------------------------------------------------------------------------

def make_icon_image(active: bool) -> Image.Image:
    image = Image.new("RGBA", (64, 64), (0, 0, 0, 0))
    draw = ImageDraw.Draw(image)
    ring = (60, 200, 255, 255) if active else (110, 110, 110, 255)
    draw.ellipse((6, 18, 58, 46), outline=ring, width=5)
    draw.ellipse((28, 27, 36, 35), fill=ring)
    return image


def port_summary(config: Config, http_port: Optional[int],
                 recv_port: Optional[int] = None) -> str:
    """実際に使っている番号だけを1行に並べる。

    OSCQuery の HTTP ポートは起動のたびに OS が選ぶので、決まるまで出せない。
    素の OSC で動いているときはそもそも存在しない。
    """
    head = f"OSCQuery TCP {http_port} / " if http_port is not None else ""
    recv = config.receive_port if recv_port is None else recv_port
    return f"{head}受信 UDP {recv} / 送信 UDP {config.send_port}"


def tooltip(config: Config, state: State, mode: str = "osc",
            http_port: Optional[int] = None,
            recv_port: Optional[int] = None) -> str:
    status = "受信中" if state.is_active() else "待機中"
    label = "OSCQuery" if mode == "oscquery" else "OSC(UDP)"
    return (f"{APP_NAME}\n"
            f"接続: {label}\n"
            f"{config.host}  {port_summary(config, http_port, recv_port)}\n"
            f"状態: {status}  受信 {state.total_messages} 件 / 生成 {state.generated} 件")


def run_tray(config: Config, state: State, log: Logger, server: Any,
             client: Any = None, mode: str = "osc",
             http_port: Optional[int] = None,
             recv_port: Optional[int] = None) -> None:
    icon = pystray.Icon(APP_NAME, make_icon_image(False),
                        tooltip(config, state, mode, http_port, recv_port))

    def on_open_log(_icon: Any, _item: Any) -> None:
        try:
            os.startfile(str(LOG_DIR))
        except Exception as exc:
            log.error(f"ログフォルダを開けませんでした: {exc}")

    def on_open_output(_icon: Any, _item: Any) -> None:
        try:
            config.output_dir.mkdir(parents=True, exist_ok=True)
            os.startfile(str(config.output_dir))
        except Exception as exc:
            log.error(f"出力フォルダを開けませんでした: {exc}")

    def on_open_data(_icon: Any, _item: Any) -> None:
        try:
            DATA_DIR.mkdir(parents=True, exist_ok=True)
            os.startfile(str(DATA_DIR))
        except Exception as exc:
            log.error(f"控えフォルダを開けませんでした: {exc}")

    def on_quit(icon_: Any, _item: Any) -> None:
        log.info("終了します")
        try:
            server.shutdown()
        except Exception:
            pass
        icon_.stop()

    def make_no_jitter(slot_number: int):
        """現在受信している値のまま、揺らぎだけ外して出力する。

        固定値では実際のパラメータを検証できないので、
        入力は本番と同じにして再現性だけを確保する。
        """
        def handler(_icon: Any, _item: Any) -> None:
            threading.Thread(
                target=generate,
                args=(slot_number, state, config, log),
                kwargs={"jitter": 0.0, "prefix": "NOJITTER_",
                        "trigger": "現在値で出力します（揺らぎなし）"},
                daemon=True).start()

        return handler

    no_jitter_menu = pystray.Menu(*[
        pystray.MenuItem(f"Object {n}", make_no_jitter(n))
        for n in range(1, SLOT_COUNT + 1)
    ])

    def make_trim(delta: Optional[float]):
        """水平倍率の微調整。delta=None でリセット。"""
        def handler(_icon: Any, _item: Any) -> None:
            with state.lock:
                state.scale_trim = 1.0 if delta is None else state.scale_trim * (1.0 + delta)
                trim = state.scale_trim
            base = config.reference_eye_height / (state.eye_height or 1.0)
            log.info(f"水平倍率の微調整: ×{trim:.4f}"
                     f"  → 実効倍率 {base * trim:.4f}"
                     f"  （確定したら reference_eye_height を "
                     f"{config.reference_eye_height * trim:.4f} に）")

        return handler

    trim_menu = pystray.Menu(
        pystray.MenuItem("+5%", make_trim(0.05)),
        pystray.MenuItem("+2%", make_trim(0.02)),
        pystray.MenuItem("+1%", make_trim(0.01)),
        pystray.MenuItem("-1%", make_trim(-0.01)),
        pystray.MenuItem("-2%", make_trim(-0.02)),
        pystray.MenuItem("-5%", make_trim(-0.05)),
        pystray.MenuItem("リセット", make_trim(None)),
    )

    def on_dump(_icon: Any, _item: Any) -> None:
        threading.Thread(target=dump_all_slots, args=(state, config, log),
                         daemon=True).start()

    def make_height_setter(slot_number: int, meters: float):
        def handler(_icon: Any, _item: Any) -> None:
            threading.Thread(target=send_height,
                             args=(client, slot_number, meters, config, log),
                             daemon=True).start()

        return handler

    height_menu = pystray.Menu(*[
        pystray.MenuItem(f"Object {n}", pystray.Menu(*[
            pystray.MenuItem(f"{m:.1f} m", make_height_setter(n, m))
            for m in HEIGHT_PRESETS
        ]))
        for n in range(1, SLOT_COUNT + 1)
    ])

    def on_reimport(_icon: Any, _item: Any) -> None:
        with state.lock:
            path = state.last_output
        if path is None:
            log.warn("まだ生成していないため読み込ませるものがありません")
            return
        threading.Thread(target=send_dolly_import,
                         args=(state, config, log, path), daemon=True).start()

    def on_refresh(_icon: Any, _item: Any) -> None:
        threading.Thread(target=refresh_from_oscquery,
                         args=(state, config, log), daemon=True).start()

    icon.menu = pystray.Menu(
        pystray.MenuItem("直前のパスをもう一度読み込ませる", on_reimport),
        pystray.MenuItem("VRChat から現在値を取得（OSCQuery）", on_refresh),
        pystray.MenuItem("高さを正確に設定（OSC送信）", height_menu),
        pystray.MenuItem("水平倍率の微調整（校正用）", trim_menu),
        pystray.MenuItem("全スロットの受信状況をログへ", on_dump),
        pystray.MenuItem("現在値で出力（揺らぎなし）", no_jitter_menu),
        pystray.MenuItem("ログフォルダを開く", on_open_log),
        pystray.MenuItem("出力フォルダを開く", on_open_output),
        pystray.MenuItem("控えフォルダを開く", on_open_data),
        pystray.MenuItem("終了", on_quit),
    )

    def refresh() -> None:
        last = None
        while True:
            time.sleep(1.0)
            active = state.is_active()
            icon.title = tooltip(config, state, mode, http_port, recv_port)
            if active != last:
                icon.icon = make_icon_image(active)
                last = active

    threading.Thread(target=refresh, daemon=True).start()
    icon.run()


# ---------------------------------------------------------------------------
# main
# ---------------------------------------------------------------------------

def main() -> int:
    config, notes = Config.load()
    log = Logger(config.debug)

    log.info(f"{APP_NAME} を起動しました")
    log.info(f"実行フォルダ: {APP_DIR}")
    for note in notes:
        log.info(note)
    log.info(f"送信ポート: {config.send_port}"
             f"（受信ポートは接続方法が決まってから確定します）")
    if config.output_dir_note:
        log.warn(config.output_dir_note)
    log.info(f"出力先: {config.output_dir}")
    log.info(f"デバッグ出力: {'ON' if config.debug else 'OFF'}")
    log.info(f"基線={PROBE_BASELINE} m  ρ={config.rho} m  "
             f"揺らぎ既定=±{int(DEFAULT_MENU['Random'])}%（アバターから届けばそちらを使う）")
    log.info("基線は Unity 側 Object_N/ProbeRig/ProbeB の Position X と一致させること")

    state = State()
    disp = build_dispatcher(state, config, log)

    mode, service, server, recv_port = establish_transport(config, log, disp)
    if server is None:
        log.close()
        return 1

    threading.Thread(target=server.serve_forever, daemon=True).start()
    log.info(f"OSC の待ち受けを開始しました（{config.host}:{recv_port}）")

    # 起動直後に現在値を取りに行く。素の OSC では触っていない項目が
    # 届かないため、ここで埋められると読み込み直しが要らなくなる。
    threading.Thread(target=refresh_from_oscquery,
                     args=(state, config, log), daemon=True).start()

    client = None
    try:
        client = udp_client.SimpleUDPClient(config.host, config.send_port)
        with state.lock:
            state.client = client
        log.info(f"OSC 送信先: {config.host}:{config.send_port}")
        log.info(f"  生成後の自動読み込み: {'ON' if config.auto_import else 'OFF'}")
        delay = config.auto_play_delay
        if delay is None or delay < 0:
            log.info("  自動再生: しない")
        elif delay == 0:
            log.info("  自動再生: 即時")
        else:
            log.info(f"  自動再生: {delay} 秒後")
        log.info(f"  Confirm 時点の比較用も出力: "
                 f"{'ON' if config.write_at_press else 'OFF'}")
    except Exception as exc:
        log.warn(f"OSC 送信クライアントを準備できませんでした: {exc}")

    try:
        http_port = getattr(service, "http_port", None) if service else None
        run_tray(config, state, log, server, client, mode, http_port, recv_port)
    finally:
        if service is not None:
            try:
                service.stop()
            except Exception:
                pass
        try:
            server.server_close()
        except Exception:
            pass
        log.info("終了しました")
        log.close()

    return 0


if __name__ == "__main__":
    sys.exit(main())
