"""계산 항목 (SPEC 5장)."""
import math

# (key, 이름, 단위, 확인필요)
DERIVED = [
    ("wheel_v", "휠 선속도", "m/s", True),
    ("dp", "분사 차압 ΔP", "bar", True),
    ("vg1", "진공도 VG1", "Torr", True),
    ("vg2", "진공도 VG2", "Torr", True),
]


def derived(v, wheel_d_mm):
    return {
        "wheel_v": math.pi * wheel_d_mm / 1000 * v["wheel_rpm"] / 60 if wheel_d_mm else None,
        "dp": v["p_upper"] - v["p_lower"],
        # TODO(확인필요): 진공도 표기(가수 소수 자릿수·지수 부호) 현장 확인 전 임시 식
        "vg1": v["vg1_mant"] * 10.0 ** v["vg1_exp"],
        "vg2": v["vg2_mant"] * 10.0 ** v["vg2_exp"],
    }
