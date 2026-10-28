import os
import math
from PIL import Image, ImageDraw

# -------------------------------------------------------------
# Glink 纯色圆角图标渲染引擎
# -------------------------------------------------------------

def bezier_cubic(p0, p1, p2, p3, steps=30):
    pts = []
    for i in range(steps + 1):
        t = i / steps
        u = 1 - t
        tt = t * t
        uu = u * u
        uuu = uu * u
        ttt = tt * t
        x = uuu * p0[0] + 3 * uu * t * p1[0] + 3 * u * tt * p2[0] + ttt * p3[0]
        y = uuu * p0[1] + 3 * uu * t * p1[1] + 3 * u * tt * p2[1] + ttt * p3[1]
        pts.append((x, y))
    return pts

def get_spark_polygon(center_x, center_y, size=48, corner_r=3, steps=40):
    # 四芒灵感星（全圆角与三次贝塞尔内凹过渡）
    # 顶部 (cx, cy - size), 底部 (cx, cy + size), 左 (cx - size, cy), 右 (cx + size, cy)
    # 控制点控制凹度
    cx, cy = center_x, center_y
    top = (cx, cy - size)
    bottom = (cx, cy + size)
    left = (cx - size, cy)
    right = (cx + size, cy)
    
    # 内凹深度控制因子
    waist = size * 0.38
    
    # 顺时针构建四段内凹贝塞尔曲线
    # 1. top -> right
    c1 = (cx, cy - waist)
    c2 = (cx + waist, cy)
    seg1 = bezier_cubic(top, (cx, cy - waist*0.7), (cx + waist*0.7, cy), right, steps)
    
    # 2. right -> bottom
    seg2 = bezier_cubic(right, (cx + waist*0.7, cy), (cx, cy + waist*0.7), bottom, steps)
    
    # 3. bottom -> left
    seg3 = bezier_cubic(bottom, (cx, cy + waist*0.7), (cx - waist*0.7, cy), left, steps)
    
    # 4. left -> top
    seg4 = bezier_cubic(left, (cx - waist*0.7, cy), (cx, cy - waist*0.7), top, steps)
    
    poly = seg1 + seg2[1:] + seg3[1:] + seg4[1:]
    return poly

def get_g_stroke_polygon(cx=256, cy=256, r=132, stroke_w=46, theta_start_deg=-38, bar_x_end=316, steps=180):
    # 计算 G 笔画的高精度外轮廓多边形（含圆弧内外壁与两个端头半圆）
    hw = stroke_w / 2.0
    r_outer = r + hw
    r_inner = r - hw
    
    theta_start = math.radians(theta_start_deg)
    # 起点中心
    p0 = (cx + r * math.cos(theta_start), cy + r * math.sin(theta_start))
    
    # 从起点沿外圆弧顺时针（屏幕坐标系顺时针相当于数学负方向，但在屏幕坐标中，y向下）
    # 起点在第一象限(y < cy)，顺时针经过上方、左侧、下方、到右侧水平点 theta = 0
    # 角度变化：从 theta_start (负值) 逆时针数学角度增加： theta 从 -38° 经过 -90°(顶) 不对！
    # 在屏幕坐标系：
    # cos(theta), sin(theta):
    # theta = 0 -> (cx+r, cy) 右
    # theta = 90 -> (cx, cy+r) 下
    # theta = 180 -> (cx-r, cy) 左
    # theta = 270 -> (cx, cy-r) 顶
    # theta = 322 (-38°) -> (cx + r*cos, cy + r*sin) 右上
    # G 的笔画应该从右上 (322°) 经过 顶 (270°), 左 (180°), 底 (90°), 到 右 (0°/360°)
    # 这在屏幕坐标系中是顺时针方向递减（322° -> 0° 即旋转了 322度），
    # 或者逆时针角度递减： 322° 经 270°、180°、90° 到 0°。
    
    # 外圆弧点集：从 322° 递减到 0°
    outer_pts = []
    # 采样角度从 322° 递减到 0°
    start_angle = 360 + theta_start_deg if theta_start_deg < 0 else theta_start_deg
    total_angle = start_angle # 比如 322 度
    for i in range(steps + 1):
        ang = math.radians(start_angle - (start_angle * i / steps))
        x = cx + r_outer * math.cos(ang)
        y = cy + r_outer * math.sin(ang)
        outer_pts.append((x, y))
        
    # 到达右侧中点外壁：(cx + r_outer, cy) = (388 + 23, 256) = (411, 256)
    # 横梁上边缘：从 (cx + r_outer, cy - hw) 向左平移到 (bar_x_end, cy - hw)
    # 注意圆弧到达 0° 时，外圆弧与横梁平滑衔接：
    # 实际上横梁的高度是 [cy - hw, cy + hw]
    # 在 0° 处，右侧外圆弧正好落在 (cx + r_outer, cy)，我们用半圆端角衔接或者自然平滑转折。
    
    # 我们用点画法/高分辨率超采样生成高精度图形，最完美的方案是利用 Pillow 绘制路径或超大分辨率下的多圆弧拼接。
    pass

def render_high_res_icon(size=512, 
                         bg_color="#151413", 
                         symbol_color="#E8956B", 
                         scale=4, 
                         has_bg=True, 
                         rx=112):
    # 超采样渲染：在 512 * scale 像素下绘制几何图形，然后高质量降采样到 size
    w = 512 * scale
    h = 512 * scale
    im = Image.new("RGBA", (w, h), (0, 0, 0, 0))
    draw = ImageDraw.Draw(im)
    
    s = scale
    
    # 1. 绘制圆角背景
    if has_bg:
        margin = 32 * s
        bg_rx = rx * s
        draw.rounded_rectangle((margin, margin, w - margin, h - margin), 
                               radius=bg_rx, 
                               fill=bg_color)
                               
    # 2. 绘制 G 主体
    cx = 256 * s
    cy = 256 * s
    r = 132 * s
    stroke_w = 46 * s
    hw = stroke_w / 2.0
    
    # G 的中心线轨迹：从角度 322° 递减到 0°，再向左画横梁到 316*s
    # 为保证极致平滑，我们沿中心线以步长 0.5px 绘制直径为 stroke_w 的圆形，形成完美的膨胀圆管（Swept Sphere/Stroke）
    start_deg = 322.0
    sweep_deg = 322.0
    
    # 计算圆弧步数
    arc_length = 2 * math.pi * r * (sweep_deg / 360.0)
    steps = int(arc_length / (1.5 * s))
    
    for i in range(steps + 1):
        deg = start_deg - (sweep_deg * i / steps)
        rad = math.radians(deg)
        x = cx + r * math.cos(rad)
        y = cy + r * math.sin(rad)
        draw.ellipse((x - hw, y - hw, x + hw, y + hw), fill=symbol_color)
        
    # 横梁：从 (cx + r, cy) 到 (316*s, cy)
    bar_start_x = cx + r
    bar_end_x = 316 * s
    bar_steps = int(abs(bar_start_x - bar_end_x) / (1.5 * s))
    for i in range(bar_steps + 1):
        x = bar_start_x - (bar_start_x - bar_end_x) * (i / bar_steps)
        y = cy
        draw.ellipse((x - hw, y - hw, x + hw, y + hw), fill=symbol_color)
        
    # 3. 绘制核心四芒微光 (Spark)
    # 中心位于 (244*s, 256*s), 尺寸 48*s
    spark_poly = get_spark_polygon(244 * s, 256 * s, size=48 * s, steps=60)
    draw.polygon(spark_poly, fill=symbol_color)
    
    # 降采样到目标尺寸
    res = im.resize((size, size), Image.Resampling.LANCZOS)
    return res

def export_all():
    out_dir = r"x:\AI project\Glink\design"
    assets_dir = r"x:\AI project\Glink\Moodia\Assets"
    os.makedirs(out_dir, exist_ok=True)
    
    # 渲染不同主题的 512x512 高清 PNG
    # 1. 暮光暗色版 (Default Twilight Dark)
    img_dark = render_high_res_icon(512, bg_color="#151413", symbol_color="#E8956B", has_bg=True)
    img_dark.save(os.path.join(out_dir, "glink-icon-dark-512.png"))
    
    # 2. 暮光强调色版 (Twilight Accent)
    img_accent = render_high_res_icon(512, bg_color="#E8956B", symbol_color="#151413", has_bg=True)
    img_accent.save(os.path.join(out_dir, "glink-icon-accent-512.png"))
    
    # 3. 晨纸浅色版 (Paper Light)
    img_paper = render_high_res_icon(512, bg_color="#F5F2EB", symbol_color="#9C3F2A", has_bg=True)
    img_paper.save(os.path.join(out_dir, "glink-icon-paper-512.png"))

    # 4. 夜航深蓝版 (Midnight)
    img_midnight = render_high_res_icon(512, bg_color="#111922", symbol_color="#8BCBDB", has_bg=True)
    img_midnight.save(os.path.join(out_dir, "glink-icon-midnight-512.png"))
    
    # 5. 纯透明单色版 (Monochrome Transparent)
    img_mono_white = render_high_res_icon(512, symbol_color="#FFFFFF", has_bg=False)
    img_mono_white.save(os.path.join(out_dir, "glink-symbol-white-512.png"))
    
    img_mono_accent = render_high_res_icon(512, symbol_color="#E8956B", has_bg=False)
    img_mono_accent.save(os.path.join(out_dir, "glink-symbol-accent-512.png"))

    # 6. 生成标准多尺寸 Windows ICO 文件 (含 16, 24, 32, 48, 64, 128, 256 尺寸)
    ico_sizes = [(16, 16), (24, 24), (32, 32), (48, 48), (64, 64), (128, 128), (256, 256)]
    img_dark.save(os.path.join(out_dir, "glink-app-icon.ico"), sizes=ico_sizes)
    img_accent.save(os.path.join(out_dir, "glink-app-icon-accent.ico"), sizes=ico_sizes)
    
    print("PNG & ICO assets generated successfully.")

export_all()
