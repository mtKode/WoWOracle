# Makes the app's leather backdrop (src/WowCompanion.Desktop/Assets/background.jpg and the 256 px tile background.png).
# Original art, generated from noise: embossed pebble grain, warped Voronoi wrinkles, mottling and worn, lighter edges.
# Usage: python tools/make-background.py src/WowCompanion.Desktop/Assets   (needs numpy, scipy, pillow)
import numpy as np, sys
from PIL import Image, ImageFilter
from scipy.spatial import cKDTree
from scipy.ndimage import gaussian_filter

def noise(h, w, cell, rng, wrap=False):
    gh, gw = max(2, h // cell + 2), max(2, w // cell + 2)
    if wrap:
        gh, gw = h // cell, w // cell
        g = rng.random((gh, gw))
        g = np.tile(g, (3, 3))
        big = np.asarray(Image.fromarray((g * 255).astype(np.uint8)).resize((gw * 3 * cell, gh * 3 * cell), Image.BICUBIC), float) / 255
        return big[gh * cell:gh * cell + h, gw * cell:gw * cell + w]
    g = rng.random((gh, gw))
    return np.asarray(Image.fromarray((g * 255).astype(np.uint8)).resize((gw * cell, gh * cell), Image.BICUBIC), float)[:h, :w] / 255

def creases(h, w, spacing, rng, wrap=False):
    n = int(h * w / spacing ** 2)
    pts = rng.random((n, 2)) * [h, w]
    tree = cKDTree(pts, boxsize=[h, w] if wrap else None)
    yy, xx = np.mgrid[0:h, 0:w].astype(float)
    # warp the grid so the cells wrinkle instead of looking like cracked tiles
    amp = spacing * 0.45
    wy = amp * (noise(h, w, max(4, spacing // 2), rng, wrap) - 0.5) + amp * 0.5 * (noise(h, w, max(3, spacing // 5), rng, wrap) - 0.5)
    wx = amp * (noise(h, w, max(4, spacing // 2), rng, wrap) - 0.5) + amp * 0.5 * (noise(h, w, max(3, spacing // 5), rng, wrap) - 0.5)
    qy, qx = yy + wy, xx + wx
    if wrap: qy, qx = qy % h, qx % w
    else: qy, qx = np.clip(qy, 0, h - 1), np.clip(qx, 0, w - 1)
    d, _ = tree.query(np.stack([qy.ravel(), qx.ravel()], 1), k=2, workers=-1)
    edge = (d[:, 1] - d[:, 0]).reshape(h, w)
    width = spacing * (0.05 + 0.07 * noise(h, w, spacing * 3, rng, wrap))  # creases vary in depth
    strength = 0.4 + 0.6 * noise(h, w, spacing * 4, rng, wrap)
    return strength * np.exp(-(edge / width) ** 2)

def pebbles(h, w, spacing, rng, wrap=False):
    """Height map of small rounded bumps (leather pebble grain): 1 in a bump's middle, 0 at its rim."""
    n = int(h * w / spacing ** 2)
    pts = rng.random((n, 2)) * [h, w]
    tree = cKDTree(pts, boxsize=[h, w] if wrap else None)
    yy, xx = np.mgrid[0:h, 0:w].astype(float)
    amp = spacing * 0.35
    qy = yy + amp * (noise(h, w, max(3, int(spacing)), rng, wrap) - 0.5)
    qx = xx + amp * (noise(h, w, max(3, int(spacing)), rng, wrap) - 0.5)
    if wrap: qy, qx = qy % h, qx % w
    else: qy, qx = np.clip(qy, 0, h - 1), np.clip(qx, 0, w - 1)
    d, _ = tree.query(np.stack([qy.ravel(), qx.ravel()], 1), k=2, workers=-1)
    t = (d[:, 1] - d[:, 0]) / (d[:, 1] + d[:, 0] + 1e-6)
    return np.sqrt(np.clip(t, 0, 1)).reshape(h, w)

def emboss(height, wrap=False):
    """Light from the top left: bright on slopes facing it, dark on the far side."""
    mode = 'wrap' if wrap else 'nearest'
    from scipy.ndimage import sobel
    gy, gx = sobel(height, 0, mode=mode), sobel(height, 1, mode=mode)
    return -(gx + gy) / 8

def leather(h, w, seed, wrap=False, edges=True):
    rng = np.random.default_rng(seed)
    base = np.array([24.0, 13.6, 9.6])
    mott = 0.55 * noise(h, w, 220, rng, wrap) + 0.3 * noise(h, w, 70, rng, wrap) + 0.15 * noise(h, w, 22, rng, wrap)
    mott = (mott - mott.mean()) / (mott.std() + 1e-6)
    fine = creases(h, w, 6, rng, wrap)
    mid = creases(h, w, 17, rng, wrap)
    grain = gaussian_filter(rng.standard_normal((h, w)), 0.7, mode='wrap' if wrap else 'reflect')
    # creases: dark grooves with a faint lit lip just above
    lip = np.roll(mid, -1, axis=0) - mid
    # fine wrinkles catch the light (thin lighter lines), larger creases sit in shadow with a lit upper lip
    peb = pebbles(h, w, 4.5, rng, wrap)
    peb2 = pebbles(h, w, 11, rng, wrap)
    height = 0.55 * peb + 0.45 * peb2 - 0.9 * mid - 0.4 * fine
    shade = emboss(gaussian_filter(height, 0.6, mode='wrap' if wrap else 'nearest'), wrap)
    light = (1 + 0.12 * mott + 0.20 * fine - 0.26 * mid + 0.25 * np.clip(lip, 0, None) + 0.09 * grain
             + 0.85 * shade + 0.14 * (peb - 0.5))
    img = base[None, None, :] * light[..., None]
    if edges:
        yy, xx = np.mgrid[0:h, 0:w]
        wob = 14 * noise(h, w, 40, rng) + 6 * noise(h, w, 9, rng)
        dist = np.minimum.reduce([xx + wob, w - 1 - xx + wob, h - 1 - yy + wob, yy + wob * 0.5 + 18])
        wear = np.clip(1 - dist / 30, 0, 1) ** 1.3                 # lighter, scuffed border
        rim = np.exp(-((dist - 3) / 2.2) ** 2)                      # thin dark stitch-like line inside the wear
        worn = np.array([58, 35, 18])
        img = img * (1 - wear[..., None]) + (worn * (0.85 + 0.3 * mott[..., None] * 0.3) * light[..., None]) * wear[..., None]
        img *= (1 - 0.35 * rim)[..., None]
        # gentle vignette toward the top like the original
        img *= (0.9 + 0.1 * np.clip(yy / (h * 0.35), 0, 1))[..., None]
    return np.clip(img, 0, 255).astype(np.uint8)

out = sys.argv[1]
Image.fromarray(leather(1260, 2600, 7)).save(out + '/background.jpg', quality=90, subsampling=0)
Image.fromarray(leather(256, 256, 11, wrap=True, edges=False)).save(out + '/background.png')
