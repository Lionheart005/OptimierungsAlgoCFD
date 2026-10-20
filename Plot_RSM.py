import pandas as pd
import numpy as np
import matplotlib.pyplot as plt
from mpl_toolkits.mplot3d import Axes3D

# ==========================================
# 1. KONFIGURATION
# ==========================================
csv_path = 'Simulation_Results.csv'
param_keys = ['Length', 'Width', 'MainRadius', 'WingRadius', 'TailTaper']

# Welche Parameter sollen auf den 3D-Achsen liegen?
x_param = 'Length'
y_param = 'Width'
z_target = 'Fitness' # Alternativ 'Drag'

# ==========================================
# 2. DATEN LADEN & BEREINIGEN
# ==========================================
print("Lade Daten...")
df = pd.read_csv(csv_path, sep=';')

# Filtere fehlerhafte Abstürze heraus (Fitness extrem niedrig)
df = df[df['Fitness'] > 0.1].reset_index(drop=True)

# Min/Max-Grenzen für die Normalisierung 
bounds = {key: (df[key].min(), df[key].max()) for key in param_keys}

# Bestes Modell finden (deren Werte nutzen wir für die "eingefrorenen" Parameter im 3D-Plot)
best_idx = df[z_target].idxmax() if z_target == 'Fitness' else df[z_target].idxmin()
best_model = df.loc[best_idx]

print(f"Beste bekannte Variante hat {z_target}: {best_model[z_target]:.2f}")

# ==========================================
# 3. DAS ERSATZMODELL (IDW ALGORITHMUS FÜR 3D)
# ==========================================
history_params = df[param_keys].values
history_targets = df[z_target].values

def predict_idw(x_val, y_val):
    candidate = [best_model[k] for k in param_keys]
    
    candidate[param_keys.index(x_param)] = x_val
    candidate[param_keys.index(y_param)] = y_val
    
    numerator = 0.0
    denominator = 0.0
    power = 3.0 
    
    for i, record in enumerate(history_params):
        dist_sq = 0.0
        for j, key in enumerate(param_keys):
            range_val = bounds[key][1] - bounds[key][0]
            if range_val <= 0: range_val = 1.0
            
            diff = (candidate[j] - record[j]) / range_val
            dist_sq += diff * diff
            
        if dist_sq < 1e-8:
            return history_targets[i]
            
        weight = 1.0 / (dist_sq ** (power / 2.0))
        numerator += weight * history_targets[i]
        denominator += weight
        
    return numerator / denominator if denominator > 0 else 0

# ==========================================
# 4. 3D-OBERFLÄCHE BERECHNEN
# ==========================================
print(f"Berechne Antwortfläche für {x_param} vs {y_param}...")
resolution = 50 
x_vals = np.linspace(bounds[x_param][0], bounds[x_param][1], resolution)
y_vals = np.linspace(bounds[y_param][0], bounds[y_param][1], resolution)

X, Y = np.meshgrid(x_vals, y_vals)
Z = np.zeros_like(X)

for i in range(resolution):
    for j in range(resolution):
        Z[i, j] = predict_idw(X[i, j], Y[i, j])

# ==========================================
# 5. DASHBOARD RENDERN (2D + 3D)
# ==========================================
fig = plt.figure(figsize=(16, 7)) # Breiteres Fenster für zwei Plots

# --- PLOT 1: 2D Fitness-Verlauf ---
ax1 = fig.add_subplot(1, 2, 1) # 1 Zeile, 2 Spalten, Plot Nr. 1

# Fortlaufende X-Achse für die Simulationen
x_timeline = range(1, len(df) + 1)
ax1.plot(x_timeline, df[z_target], marker='o', linestyle='-', color='#1f77b4', alpha=0.8, label='Simulationen')

# Markiere den absoluten Champion rot
ax1.scatter(best_idx + 1, df.loc[best_idx, z_target], color='red', s=120, zorder=5, label='Champion')

ax1.set_xlabel('Simulierte Variante (Chronologisch)')
ax1.set_ylabel(f'{z_target}')
ax1.set_title(f'Verlauf der {z_target} über die Optimierung')
ax1.grid(True, linestyle='--', alpha=0.6)
ax1.legend()

# --- PLOT 2: 3D Antwortfläche ---
ax2 = fig.add_subplot(1, 2, 2, projection='3d') # 1 Zeile, 2 Spalten, Plot Nr. 2

surf = ax2.plot_surface(X, Y, Z, cmap='viridis', edgecolor='none', alpha=0.7)
ax2.scatter(df[x_param], df[y_param], df[z_target], color='red', s=60, edgecolors='black', label='Echte CFD-Simulationen')

ax2.set_xlabel(x_param)
ax2.set_ylabel(y_param)
ax2.set_zlabel(f'Erwartete {z_target}')
ax2.set_title(f'RSM Antwortfläche ({x_param} vs {y_param})\n(Restliche Parameter eingefroren auf Bestwerten)')

fig.colorbar(surf, ax=ax2, shrink=0.5, aspect=5, label=z_target)
ax2.legend()

plt.tight_layout()
print("Öffne interaktives Dashboard...")
plt.show()