#!/bin/sh
# Injetado no contexto no inicio de cada sessao pelo hook SessionStart.
#
# Nao despeja o vault inteiro: um despejo grande so afoga o resto do contexto. O que entra e a
# regra que governa tudo, a lista de armadilhas pelo nome, e a contagem por pasta — o suficiente
# para saber QUE existe nota sobre a area que vai ser tocada, e qual abrir. Abrir e trabalho.
VAULT="$(dirname "$0")/../ROLLOUTLOUD-Vault"

[ -d "$VAULT" ] || exit 0

echo "=== CEREBRO DO PROJETO (ROLLOUTLOUD-Vault) ==="
echo "Regra: consultar antes de mexer, atualizar na mesma entrega."
echo "Abra a nota inteira antes de tocar a area dela; aqui vai so o mapa."
echo

echo "--- A regra que governa tudo ---"
sed -n '/^> \*\*O agente nunca/,+1p' "$VAULT/regras/A regra que governa tudo.md" 2>/dev/null
echo

echo "--- Armadilhas (abrir a que couber ANTES de mexer) ---"
ls "$VAULT/armadilhas" 2>/dev/null | sed 's/\.md$//' | grep -v '^Armadilhas$' | sed 's/^/  - /'
echo

echo "--- Decisoes (a alternativa recusada esta escrita) ---"
ls "$VAULT/decisoes" 2>/dev/null | sed 's/\.md$//' | grep -v '^Decis' | sed 's/^/  - /'
echo

echo "--- Notas por pasta ---"
for d in regras decisoes armadilhas medicoes marcos sessoes; do
    n=$(ls "$VAULT/$d" 2>/dev/null | wc -l)
    printf '  %-12s %s notas\n' "$d" "$n"
done
echo

echo "Indice: ROLLOUTLOUD-Vault/00 INDEX.md"
echo "Commits: ingles, primeira pessoa, um por atualizacao, SEM trailer de ferramenta."
