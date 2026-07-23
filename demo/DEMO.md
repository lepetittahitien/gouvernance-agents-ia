# Déroulé de démo — Gouvernance & Observabilité d'agents IA

**Durée cible : 12–15 min.** Public type : CTO / tech lead qui a (ou prépare) un agent IA en production.

Le fil rouge de la démo n'est pas « regardez toutes mes fonctionnalités » mais une seule
question posée au client dès la première minute : **« votre agent, là, maintenant — vous
savez ce qu'il fait ? »** Chaque écran répond à un morceau de cette question.

---

## Checklist T-15 min (à faire AVANT que le client arrive)

```bash
# 1. Postgres
docker compose up -d
docker exec gouvernance-agents-ia-db pg_isready -U gouvernance -d gouvernance_agents_ia

# 2. Ollama (les deux modèles doivent être là)
ollama list        # attendu : llama3.2 ET nomic-embed-text

# 3. L'API
cd src/TraceAgentApi && dotnet run
# → attendu : "Now listening on: http://localhost:5013"

# 4. Les données de démo (si base vide ou trop pauvre)
./demo/seed-demo.sh --with-evals

# 5. Contrôles finaux
curl -s localhost:5013/audit/verify | grep -q '"isIntact":true' && echo "chaîne OK"
```

Ouvrir `http://localhost:5013` dans un navigateur en plein écran. Fermer Slack.

**Répétition du moment fort** : faire une fois la séquence de falsification (§ étape 5)
la veille, pour l'avoir dans les doigts.

---

## Le déroulé

### 0. Le problème (1 min — sans écran)

À dire, en substance : *« Quand vous avez mis un service en prod, vous avez des logs, des
métriques, des alertes. Quand une entreprise met un agent IA en prod aujourd'hui, elle n'a
rien de tout ça. Elle découvre les problèmes par la facture, ou par un client mécontent.
Je vais vous montrer à quoi ressemble un agent sous surveillance. »*

### 1. La timeline d'un run (2 min) — onglet **Runs**

- Taper en live : `Quelle est la météo à Marseille ?` → **Lancer un run**.
- Pendant les ~8 s d'attente : *« l'agent décide seul d'appeler un outil externe — c'est
  précisément cette autonomie qu'il faut surveiller. »*
- Sur le résultat, pointer : chaque étape, la barre proportionnelle à la durée, les tokens.
- **Phrase clé** : *« regardez : sur X secondes de run, la majorité part dans l'appel d'API
  externe, pas dans le modèle. Sans cette timeline, vous auriez optimisé le mauvais bout. »*
- Pointer la jauge de budget en haut à droite qui vient de bouger.

### 2. La fuite de données, attrapée (2 min) — onglet **Runs**

- Cliquer sur le run avec le badge rouge **⚠ PII** (déjà en base via le seed).
- Pointer la bannière : type et nombre, `Email × N`.
- Dérouler la timeline : montrer que la fuite était **dans l'appel d'outil**, avant même
  la réponse finale.
- **Phrase clé** : *« la détection est du code déterministe — des regex, une validation de
  Luhn. Pas une IA qui surveille une IA. Même entrée, même verdict, à chaque fois. Et en
  base, on ne stocke jamais la valeur détectée : uniquement le type et le compte. Votre
  outil de conformité ne doit pas devenir lui-même un dépôt de données personnelles. »*

### 3. Le blocage, pas juste l'alerte (2 min) — même run

- Sur ce même run : pointer l'étape rouge **REFUSÉ get_weather(city=contact@client.fr)**.
- **Phrase clé** : *« là on ne détecte plus, on empêche. Une règle dit ce que cet agent a
  le droit d'appeler, et avec quelles données. L'appel n'est jamais parti. Et regardez la
  réponse finale : l'agent n'a pas planté — il a reçu le refus et l'a expliqué proprement. »*
- Mentionner : fermé par défaut — un agent inconnu n'a aucun droit ; un fichier de règles
  absent = tout refusé.

### 4. Le signal heuristique, présenté honnêtement (1 min) — onglet **Runs**

- Cliquer sur le run au badge **orange ⚠ INJ Medium**.
- **Phrase clé** : *« l'orange, c'est volontaire. La détection d'injection est heuristique :
  un score de suspicion, pas un verdict. Quiconque vous vend un détecteur d'injection
  “fiable” vous ment. Ici, l'orange dit à votre opérateur : viens juger. Le rouge dit :
  c'est établi. »* — C'est un moment de crédibilité, pas de faiblesse.

### 5. ⭐ Le moment fort : falsifier le journal devant le client (3 min) — onglet **Audit**

- Cliquer **Vérifier l'intégrité** → bannière verte, N entrées.
- *« Chaque entrée contient l'empreinte de la précédente. Maintenant, imaginons un
  administrateur — ou un attaquant — qui veut maquiller ce que l'agent a fait. »*
- Dans un terminal **visible du client**. Les commandes capturent automatiquement la
  séquence visée ET sa valeur d'origine — **rien à adapter à la main**, donc rien à rater
  sous pression (erreur commise et attrapée en répétition : falsifier une séquence et en
  « restaurer » une autre casse la chaîne pour de bon) :

```bash
# Fonction (et pas variable : une variable ne s'expanse pas en commande sous zsh) :
db() { docker exec gouvernance-agents-ia-db psql -U gouvernance -d gouvernance_agents_ia "$@"; }

# ÉTAPE A — choisir une entrée et mémoriser sa valeur d'origine :
SEQ=$(db -t -A -c "SELECT \"Sequence\" FROM audit_entries WHERE \"Action\" = 1 ORDER BY \"Sequence\" LIMIT 1;")
ORIG=$(db -t -A -c "SELECT \"Details\" FROM audit_entries WHERE \"Sequence\" = $SEQ;")
echo "séquence $SEQ — valeur d'origine : $ORIG"

# ÉTAPE B — falsifier :
db -c "UPDATE audit_entries SET \"Details\" = 'get_weather(city=Falsifiée)' WHERE \"Sequence\" = $SEQ;"
```

- Retour au navigateur → **Vérifier l'intégrité** → bannière **rouge**, séquence exacte,
  hash attendu vs recalculé.
- **Phrase clé** : *« je ne vous vends pas un journal “immuable” — sur un Postgres
  classique, ça n'existe pas, et un auditeur le sait. Ce que je vous garantis : personne
  ne peut le modifier sans que ça se voie. »*
- **Restaurer** (la valeur d'origine est dans `$ORIG`, même terminal) :

```bash
db -c "UPDATE audit_entries SET \"Details\" = '$ORIG' WHERE \"Sequence\" = $SEQ;"
```

- Re-vérifier → verte. *« Et voilà l'export que vous donnez à votre auditeur — la preuve
  d'intégrité voyage avec les données »* :

```bash
curl -s "localhost:5013/compliance/export?format=csv&redactPii=true" | head -8
```

> ⚠ Ne JAMAIS démontrer la **suppression** d'une entrée en démo : c'est irréversible,
> la chaîne resterait cassée. Seule la modification restaurée à l'identique est réversible.

### 6. La recherche d'incident (1 min) — onglet **Recherche**

- Taper : `une fuite de données personnelles` → les runs PII sortent en tête.
- **Phrase clé** : *« le mot “fuite” n'apparaît dans aucun de ces runs. C'est une recherche
  par le sens — votre équipe d'astreinte décrit l'incident avec ses mots, l'outil retrouve
  les cas comparables. »* Cliquer un résultat → il ouvre le run.

### 7. Les évals — la question du vendredi soir (1 min) — onglet **Évals**

- Montrer l'historique des scores ; ouvrir un run à 66,7 % s'il y en a un.
- **Phrase clé** : *« vous changez un prompt un vendredi à 17h. Vous déployez ? Ici, on
  rejoue le jeu de tests : une régression, c'est un cas qui passait et qui casse — pas un
  score qui frémit. Le rapport nomme le cas et l'assertion en échec. »*

### 8. La chute : « et si votre agent existe déjà ? » (2 min) — onglet **Scans externes**

- Montrer les scans de `support-bot-prod`, `rag-ingestion`, `batch-facturation`.
- **Phrase clé** : *« tout ce que je viens de montrer tournait sur MON agent de démo. Mais
  votre agent à vous existe déjà, construit par quelqu'un d'autre, et vous n'allez pas le
  réécrire. Ces trois systèmes-là non plus : ils appellent trois endpoints HTTP, via un
  proxy ou un webhook — sans toucher une ligne de leur code. »*
- En live, dans le terminal :

```bash
curl -s -X POST localhost:5013/scan -H "Content-Type: application/json" \
  -d '{"text":"Le RIB du client : FR76 3000 6000 0112 3456 7890 189","source":"demo-live"}' | python3 -m json.tool
```

- Rafraîchir l'onglet → la ligne vient d'apparaître. *« Voilà. C'est ça, l'installation
  chez vous : on branche, on ne réécrit pas. »*

---

## Objections fréquentes

| Objection | Réponse |
|---|---|
| « Notre agent tourne sur GPT-4/Claude/Mistral, pas Ollama » | Les garde-fous sont agnostiques : ils traitent du texte en entrée/sortie. Ollama n'est là que pour l'agent de démo. Le champ coût € est déjà prévu pour un provider facturé. |
| « Le journal est vraiment immuable ? » | Non — et méfiez-vous de qui dit oui sur un Postgres. Il est *infalsifiable en douce* : toute modification est détectable et prouvable. Pour durcir : révoquer UPDATE/DELETE au niveau SQL, ancrer le dernier hash à l'extérieur. |
| « Votre détecteur d'injection rate des attaques ? » | Oui, certainement — c'est heuristique et on l'affiche comme tel (score, orange). Il attrape les patterns connus et donne un signal à corréler. La défense en profondeur, ce sont les *policies* : même si l'injection passe, l'agent n'a pas le droit d'appeler ce qu'on ne lui a pas ouvert. |
| « Ça tient la charge ? » | La démo est mono-instance, assumée. Les points durs sont identifiés et documentés dans le code (verrou d'audit → verrou consultatif Postgres en multi-instance). |
| « Et nos données partent où ? » | Nulle part. Tout tourne chez vous : modèles locaux possibles, Postgres chez vous, et les scans externes ne stockent jamais le texte analysé — seulement le verdict. |

## Après la démo

- Laisser le lien du repo public : `github.com/lepetittahitien/gouvernance-agents-ia`
- La question de qualification à poser : *« aujourd'hui, si votre agent fait n'importe
  quoi à 3h du matin, qui le voit, et quand ? »*
