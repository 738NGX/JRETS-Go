import json
import math

# ==========================================
# 1. 核心配置区
# ==========================================
CONFIG = {
    "input_file": "saikyo.geojson",      # 输入文件名
    "output_prefix": "saikyo",      # 输出文件前缀
    
    # --- 物理逻辑 ---
    "is_loop": False,                     # 环线设为 True, 射线设为 False
    "start_ref_pt": [139.7281603, 35.6192342],  # 起点参考 (如: 东京)
    "end_ref_pt": [139.4830911, 35.9069134],    # 终点参考 (如: 热海, 环线可不填)
    "guide_pt": None,                     # 引导点 (可选, 用于引导拼接方向)
    
    # --- 车站逻辑 ---
    "id_start": 32101,                    # 起始 ID
    "id_mode": "forward",                 # "forward", "reverse", "yamanote_inner"
    "exclude_pts": [                      # 排除坐标 (如高轮Gateway)
    ],
    "manual_stations": [                  # 手动补站 (解决OSM漏站问题)
        {"name": "南与野", "coords": [139.6311110, 35.8679013]}
    ],
    
    # --- 算法参数 ---
    "overlap_threshold": 1.0,             # 轨道拼接容差 (km)
    "station_group_dist": 0.5             # 车站去重阈值 (km)
}

# ==========================================
# 2. 核心算法区 (逻辑已补全)
# ==========================================
def haversine(lon1, lat1, lon2, lat2):
    R = 6371.0
    p1, p2 = math.radians(lat1), math.radians(lat2)
    dp, dl = math.radians(lat2-lat1), math.radians(lon2-lon1)
    a = math.sin(dp/2)**2 + math.cos(p1)*math.cos(p2)*math.sin(dl/2)**2
    return 2 * R * math.atan2(math.sqrt(a), math.sqrt(1-a))

def process():
    # A. 加载数据
    with open(CONFIG["input_file"], 'r', encoding='utf-8') as f:
        data = json.load(f)
    segments = [f['geometry']['coordinates'] for f in data['features'] if f['geometry']['type'] == 'LineString']
    
    # B. 轨道拼接
    full_track_raw = []
    curr_pt = CONFIG["start_ref_pt"]
    remaining = segments[:]
    
    # 引导步
    if remaining:
        best_idx, max_score, is_rev = -1, -float('inf'), False
        for i, s in enumerate(remaining):
            for pt, rev in [(s[0], False), (s[-1], True)]:
                if haversine(curr_pt[0], curr_pt[1], pt[0], pt[1]) < 0.3:
                    score = 0
                    if CONFIG["guide_pt"]:
                        other = s[-1] if not rev else s[0]
                        score = -haversine(other[0], other[1], CONFIG["guide_pt"][0], CONFIG["guide_pt"][1])
                    if score > max_score: max_score, best_idx, is_rev = score, i, rev
        if best_idx != -1:
            seg = remaining.pop(best_idx)
            full_track_raw.extend(seg[::-1] if is_rev else seg)
            curr_pt = full_track_raw[-1]

    # 贪婪拼接
    while remaining:
        best_idx, min_d, rev = -1, float('inf'), False
        for i, s in enumerate(remaining):
            ds, de = haversine(curr_pt[0], curr_pt[1], s[0][0], s[0][1]), haversine(curr_pt[0], curr_pt[1], s[-1][0], s[-1][1])
            if ds < min_d: min_d, best_idx, rev = ds, i, False
            if de < min_d: min_d, best_idx, rev = de, i, True
        if min_d > CONFIG["overlap_threshold"]: break
        seg = remaining.pop(best_idx)
        full_track_raw.extend(seg[::-1][1:] if rev else seg[1:])
        curr_pt = full_track_raw[-1]
        # 射线早停判定
        if not CONFIG["is_loop"] and haversine(curr_pt[0], curr_pt[1], CONFIG["end_ref_pt"][0], CONFIG["end_ref_pt"][1]) < 0.3:
            break

    # C. 物理对齐 (核心修复点)
    # 1. 找起点索引
    min_ds, s_idx = float('inf'), 0
    for i, pt in enumerate(full_track_raw):
        d = haversine(pt[0], pt[1], CONFIG["start_ref_pt"][0], CONFIG["start_ref_pt"][1])
        if d < min_ds: min_ds, s_idx = d, i
    
    if CONFIG["is_loop"]:
        # 环线模式：旋转轨道，首尾相连
        full_track = full_track_raw[s_idx:] + full_track_raw[1:s_idx+1]
    else:
        # 射线模式：从起点开始切，找到终点索引并截断
        temp_track = full_track_raw[s_idx:]
        min_de, e_idx = float('inf'), len(temp_track) - 1
        for i, pt in enumerate(temp_track):
            d = haversine(pt[0], pt[1], CONFIG["end_ref_pt"][0], CONFIG["end_ref_pt"][1])
            if d < min_de: min_de, e_idx = d, i
        full_track = temp_track[:e_idx + 1]

    # D. 位移计算
    track_dist_map = []
    total_len = 0.0
    track_dist_map.append((0.0, full_track[0]))
    for i in range(1, len(full_track)):
        total_len += haversine(full_track[i-1][0], full_track[i-1][1], full_track[i][0], full_track[i][1])
        track_dist_map.append((total_len, full_track[i]))

    # E. 车站采集与手动补站
    all_stations = []
    # 注入手动补救点
    for ms in CONFIG["manual_stations"]:
        all_stations.append({'name': ms['name'], 'coords': ms['coords']})
    # 采集 GeoJSON 车站点
    for f in data['features']:
        if f['geometry']['type'] == 'Point':
            c = f['geometry']['coordinates']
            if any(haversine(c[0], c[1], ex[0], ex[1]) < 0.4 for ex in CONFIG["exclude_pts"]): continue
            all_stations.append({'name': f['properties'].get('name', 'Unknown'), 'coords': c})

    # 投影车站到位移轴
    projected = []
    for s in all_stations:
        min_pd, best_d = float('inf'), 0.0
        for d, tp in track_dist_map:
            dist = haversine(s['coords'][0], s['coords'][1], tp[0], tp[1])
            if dist < min_pd: min_pd, best_d = dist, d
        if min_pd < 0.4: # 只保留靠近轨道的车站
            projected.append({'name': s['name'], 'coords': s['coords'], 'dist': best_d})

    # F. 排序去重
    projected.sort(key=lambda x: x['dist'])
    unique = []
    for p in projected:
        if not unique or (p['dist'] - unique[-1]['dist'] > CONFIG["station_group_dist"]):
            unique.append(p)

    # G. ID 映射
    final_stations = {}
    count = len(unique)
    for i, s in enumerate(unique):
        if CONFIG["id_mode"] == "forward": sid = CONFIG["id_start"] + i
        elif CONFIG["id_mode"] == "reverse": sid = CONFIG["id_start"] - i
        elif CONFIG["id_mode"] == "yamanote_inner": sid = CONFIG["id_start"] if i == 0 else CONFIG["id_start"] + (count - i)
        else: sid = i
        final_stations[sid] = {"name": s['name'], "displacement": round(s['dist'], 4), "coordinates": s['coords']}

    # H. 导出
    prefix = CONFIG["output_prefix"]
    with open(f"{prefix}_stations.json", 'w', encoding='utf-8') as f:
        json.dump(final_stations, f, indent=2, ensure_ascii=False)
    with open(f"{prefix}_route.json", 'w', encoding='utf-8') as f:
        json.dump({str(round(d, 6)): c for d, c in track_dist_map}, f, indent=2, ensure_ascii=False)

    print(f"[{prefix}] 处理成功！识别车站: {len(final_stations)}。总长: {round(total_len, 2)} km")

if __name__ == "__main__":
    process()