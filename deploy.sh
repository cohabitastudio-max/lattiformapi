#!/bin/bash
# ============================================================
#  LattiformAPI — Script de deploy a GitHub + Render.com
#  Ejecuta desde la carpeta LattiformAPI/
# ============================================================

set -e
echo "🚀 LattiformAPI — Deploy Script"
echo "================================"

# 1. Verificar que estamos en la carpeta correcta
if [ ! -f "LattiformAPI.csproj" ]; then
  echo "❌ Error: ejecuta este script desde la carpeta LattiformAPI/"
  exit 1
fi

# 2. Pedir usuario de GitHub
read -p "👤 Tu usuario de GitHub: " GH_USER
REPO_NAME="lattiformapi"
REPO_URL="https://github.com/$GH_USER/$REPO_NAME.git"

echo ""
echo "📋 Pasos manuales ANTES de continuar:"
echo "   1. Ve a https://github.com/new"
echo "   2. Nombre del repo: $REPO_NAME"
echo "   3. Privado o público (tu elección)"
echo "   4. NO inicialices con README"
echo "   5. Copia la URL del repo: $REPO_URL"
echo ""
read -p "¿Creaste el repo en GitHub? (s/n): " CREATED

if [ "$CREATED" != "s" ]; then
  echo "Ve a https://github.com/new y crea el repo primero."
  exit 0
fi

# 3. Init git si no existe
if [ ! -d ".git" ]; then
  echo "📁 Inicializando git..."
  git init
  git branch -M main
fi

# 4. Add & commit
echo "📝 Añadiendo archivos..."
git add .
git status
echo ""
git commit -m "LattiformAPI v1.0 — PicoGK headless geometry microservice

- POST /api/generate — TPMS + Lattice geometry generation
- POST /api/tpms    — Gyroid, Schwarz-P/D, Lidinoid, Neovius, IWP
- POST /api/lattice — BCC, FCC, Octet, Kelvin, Diamond
- GET  /api/types   — list available geometries
- GET  /health      — health check

Stack: .NET 8 + PicoGK 1.7.7 headless + Docker" || true

# 5. Push a GitHub
echo ""
echo "📤 Subiendo a GitHub..."
git remote remove origin 2>/dev/null || true
git remote add origin "$REPO_URL"
git push -u origin main

echo ""
echo "✅ Código en GitHub: $REPO_URL"
echo ""
echo "🌐 SIGUIENTE PASO — Deploy en Render.com:"
echo "   1. Ve a https://render.com"
echo "   2. New → Web Service"
echo "   3. Conecta tu repo: $GH_USER/$REPO_NAME"
echo "   4. Runtime: Docker (auto-detectado)"
echo "   5. Plan: Starter (\$7/mes) o Free (cold start)"
echo "   6. Click 'Create Web Service'"
echo ""
echo "📍 Tu URL final será: https://lattiformapi.onrender.com"
echo "   (ya configurada en Lattiform frontend)"
echo ""
echo "🔍 Para probar después del deploy:"
echo "   curl https://lattiformapi.onrender.com/health"
