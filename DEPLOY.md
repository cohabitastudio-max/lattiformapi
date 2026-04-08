# 🚀 Deploy LattiformAPI en Render.com

## Pasos (10 minutos)

### 1. Crear repositorio en GitHub

```bash
# En tu máquina local, desde la carpeta del proyecto:
cd LattiformAPI

git init
git add .
git commit -m "LattiformAPI v1.0 — PicoGK headless microservice"

# Crear repo en github.com → New repository → "lattiformapi" (privado)
git remote add origin https://github.com/TU_USUARIO/lattiformapi.git
git push -u origin main
```

### 2. Deploy en Render.com

1. Ve a **https://render.com** → Sign up con GitHub
2. Click **"New +"** → **"Web Service"**
3. Conecta tu repo `lattiformapi`
4. Render detecta el `Dockerfile` automáticamente
5. Configuración:
   - **Name:** `lattiformapi`
   - **Branch:** `main`
   - **Runtime:** Docker (auto-detectado)
   - **Plan:** Starter ($7/mes) para desarrollo
6. Click **"Create Web Service"**
7. Espera ~5 minutos para el primer build

### 3. Obtener la URL

Render te da una URL como:
```
https://lattiformapi.onrender.com
```

### 4. Actualizar Lattiform Frontend

En `pages/Designer.jsx`, la línea:
```js
const PICOGK_API = "https://lattiformapi.onrender.com";
```
...ya apunta a esta URL exacta. ¡No necesitas cambiar nada!

### 5. Probar

```bash
# Health check
curl https://lattiformapi.onrender.com/health

# Generar geometría de prueba
curl -X POST https://lattiformapi.onrender.com/api/generate \
  -H "Content-Type: application/json" \
  -d '{
    "type": "gyroid",
    "resolution": "medium",
    "bounding_box": [50, 50, 50],
    "params": {
      "cell_size": 8,
      "wall_thickness": 0.8
    }
  }'
```

## Notas importantes

- **Cold start:** Render.com (plan gratuito) hiberna después de 15 min sin uso → primer request tarda ~30s. Con plan Starter ($7/mes) siempre activo.
- **PicoGK native lib:** El Dockerfile descarga `picogk.so` desde GitHub releases. Si el URL cambia, actualiza el Dockerfile.
- **RAM:** Gyroid medium (50mm) usa ~200MB. Para `resolution: "high"` en piezas grandes, usa plan Standard (2GB).

## Alternativas gratuitas

- **Fly.io:** `flyctl deploy` — 3 VMs gratis, mejor cold start que Render free
- **Google Cloud Run:** free tier 2M requests/mes, escala a cero
- **Railway.app:** $5 créditos/mes gratis

## Estructura del proyecto para GitHub

```
lattiformapi/
├── Dockerfile          ← ya listo
├── render.yaml         ← ya listo
├── LattiformAPI.csproj ← ya listo
├── Program.cs          ← ya listo
└── DEPLOY.md           ← este archivo
```
