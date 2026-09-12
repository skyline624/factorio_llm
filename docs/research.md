# Références et enseignements pour la conception

Recherche documentaire du 12 septembre 2026. L'implémentation et les essais sont autorisés selon le [plan d'exécution](execution-plan.md). Les constats ci-dessous portent sur les documents, le code public consultable et l'API livrée avec le jeu ; ils ne qualifient pas automatiquement notre implémentation. Aucun projet de référence n'a été installé ou exécuté pendant cette recherche documentaire.

Contexte validé : Factorio 2.0.77 de base avec attaques actives, `glm-5.3-flash:cloud` via Ollama, C# majoritaire, synthèse spatiale complète sans gabarits ni placement LLM, stocks exacts de toute l'usine propre connue et ennemis soumis à la visibilité normale du personnage/radars. La V1 utilise tapis et tuyaux. La mort entraîne réapparition et récupération dans la même partie. Le même avatar doit permettre IA autonome et contrôle du pilote ; ces mécanismes nécessitent une qualification réelle.

## Références prioritaires : calculs et spatialisation

| Référence | Apport concret | Limites et compatibilité | Licence relevée |
|---|---|---|---|
| [Foreman2](https://github.com/DanielKote/Foreman2) | Graphes de production, besoins en machines, combustibles, débits et diagnostics de manque/surproduction en C# | Application graphique Windows Forms ; son graphe n'est pas une implantation sur la carte. Le projet courant cible .NET 10 et inclut des presets Factorio 2.0 | [Blue Oak Model License 1.0.0](https://github.com/DanielKote/Foreman2/blob/Main/LICENSE.md) |
| [YAFC Community Edition](https://github.com/Yafc-CE/yafc-ce) | Dépendances, accessibilité, jalons et bilans de production en C# ; présence d'un exemple CLI sans interface graphique | Support 2.0+ annoncé. Signatures internes non stables et approximations du calcul ; ne fournit pas un placement autonome | [GPL v3](https://github.com/Yafc-CE/yafc-ce/blob/master/LICENSE) |
| [FactorioTools](https://github.com/joelverhagen/FactorioTools) | Véritable spatialisation C# de champs pétroliers : orientations, tuyaux, poteaux et beacons ; stratégies A*, Delaunay et Steiner | Domaine limité au pétrole. L'exporteur consulté utilise encore une version blueprint 1.1.101.1 et les directions 0/2/4/6 ; qualification 2.0.77 nécessaire | [MIT](https://github.com/joelverhagen/FactorioTools/blob/main/LICENSE) |
| [FactorioLab](https://github.com/factoriolab/factoriolab) | Calculateur de ratios et de besoins pouvant servir de comparaison indépendante | TypeScript/Angular ; données 2.0 présentes, sans preuve d'identité avec notre 2.0.77. Aucun pilotage du jeu ou placement complet | [MIT](https://github.com/factoriolab/factoriolab/blob/main/LICENSE) |
| [Factorio Draftsman](https://github.com/redruin1/factorio-draftsman) | Modèle, manipulation et validation de blueprints ; corpus de cas limites de format | Python. Prise en charge des formats 1.x/2.0 documentée ; l'utilisateur doit fournir la logique de génération. La migration 1.x vers 2.0 n'est pas complète | [MIT](https://github.com/redruin1/factorio-draftsman/blob/main/LICENSE) |
| [Factorio-SAT](https://github.com/R-O-C-K-E-T/Factorio-SAT) | Génération de réseaux de tapis par contraintes ; exemples sur directions, souterrains et raccordements | Python, compatibilité 2.0 non établie. Le projet ne démontre pas la conformité complète des contraintes aux règles du jeu ; les problèmes avec splitters peuvent devenir coûteux | [GPL v3](https://github.com/R-O-C-K-E-T/Factorio-SAT/blob/master/LICENSE) |

Priorité proposée : étudier Foreman2 et YAFC CE pour le calcul, puis FactorioTools pour les interfaces du composant spatial. Cela ne constitue pas une décision de réutilisation de leur code. Le code original du projet est sous [MIT](../LICENSE) ; tout choix de dépendance ou reprise de code doit préserver les conditions propres à sa source. L'étude d'un algorithme ne signifie pas incorporation de son implémentation.

Points de lecture précis : [projet Foreman2](https://github.com/DanielKote/Foreman2/blob/Main/Foreman/Foreman.csproj), [exemple CLI YAFC CE](https://github.com/Yafc-CE/yafc-ce/blob/master/CommandLineToolExample/Program.cs), [exporteur FactorioTools](https://github.com/joelverhagen/FactorioTools/blob/main/src/FactorioTools.Serialization/OilField/Steps/GridToBlueprintString.cs), [directions FactorioTools](https://github.com/joelverhagen/FactorioTools/blob/main/src/FactorioTools/Data/Direction.cs), [données FactorioLab 2.0](https://github.com/factoriolab/factoriolab/blob/main/public/data/2.0/data.json), [versions Draftsman](https://github.com/redruin1/factorio-draftsman/releases).

## Agents et contrôle réactif

| Référence | Mécanisme à retenir | Écart avec notre objectif |
|---|---|---|
| [AI Player v3](https://github.com/thedemon117/ai-player-v3) | Mod Factorio 2.0 : personnage IA, perception, registre de machines, compétences comme extraction et installation de mineur/fonderie ; compatible avec un fournisseur Ollama | Python/Lua ; téléportations documentées et accès à du Lua arbitraire. Aucun lancement autonome depuis zéro avec attaques et règles normales démontré dans les éléments consultés. 2.0.77 non qualifié. MIT annoncé dans le README et le [portail](https://mods.factorio.com/mod/ai-player-v3) |
| [Voyager](https://voyager.minedojo.org/) | Bibliothèque persistante de compétences, progression graduelle, mémoire et récupération après erreurs | Résultats Minecraft avec GPT-4 ; génération de code et critique de réussite par LLM à remplacer chez nous par compétences fermées et preuves moteur. [Code MIT](https://github.com/MineDojo/Voyager/blob/main/LICENSE) |
| [SayCan](https://say-can.github.io/) | Combiner intérêt d'une action pour l'objectif et faisabilité dans l'état présent | Robotique avec compétences préexistantes et faisabilité apprise. Notre adaptation utiliserait des contrôles déterministes C#/Lua. Le [notebook public](https://github.com/google-research/google-research/blob/master/saycan/SayCan-Robot-Pick-Place.ipynb) n'est pas le système robotique complet ; licence Apache-2.0 indiquée dans son en-tête |
| [BehaviorTree.CPP](https://behaviortree.dev/docs/tutorial-basics/tutorial_04_sequence/) | Actions non bloquantes, états en cours/réussite/échec, contrôle réactif et interruption des actions longues | Bibliothèque C++, pas un agent Factorio. Retenir les principes pour une exécution C# qui réagit aux attaques sans attendre Ollama. [Code MIT](https://github.com/BehaviorTree/BehaviorTree.CPP) |
| [PDDLStream](https://arxiv.org/abs/1802.08705) | Articuler un plan symbolique avec des procédures externes de géométrie, de mouvement et de vérification | Travaux robotiques, domaine à modéliser ; aucune intégration Factorio prête à employer. [Implémentation GPL-3.0](https://github.com/caelan/pddlstream) |

Ces références confortent une proposition, sans la prouver pour Factorio : le LLM propose des objectifs stratégiques et leurs priorités, le calcul détermine les plans et les positions, l'exécuteur réalise des compétences testables, et une boucle prioritaire traite les menaces. Le catalogue de compétences de l'exécuteur ne limite pas le modèle à une liste prédéfinie d'objectifs.

## Retours sur les références initiales

[FLE v0.3.0](https://jackhopkins.github.io/factorio-learning-environment/versions/0.3.0.html) décrit notamment les observations périmées, les erreurs de placement, les transports manuels et les effets de stocks tampons. Sa mesure avec période sans intervention inspire nos critères de débit soutenu. Le [README du tag v0.3.0](https://raw.githubusercontent.com/JackHopkins/factorio-learning-environment/v0.3.0/README.md) cible Factorio 1.1.110, tandis que le [README actuel](https://raw.githubusercontent.com/JackHopkins/factorio-learning-environment/main/README.md) annonce une version 2.0 récente : les scripts historiques ne doivent pas être considérés comme compatibles 2.0.77 par défaut. [Licence MIT du tag](https://raw.githubusercontent.com/JackHopkins/factorio-learning-environment/v0.3.0/LICENSE).

La [démonstration SUPCON](https://global.supcon.com/posts/a-thinking-factory-comes-alive-llm-agents-in-the-world-of-factorio) utilise un environnement simplifié avec ressources infinies et sans ennemis. Elle éclaire la boucle de télémétrie, mais ne valide pas notre campagne. Le [mod MQTT](https://github.com/supcon-international/Factorio-MQTT-mod) présente dans le code consulté des effets de bord à éviter : suppression de contenus de coffres à positions fixes et comptage de production déduit d'un échantillonnage de progression. Les conditions de licence affichées sont incohérentes entre certains en-têtes et le portail ; aucune copie n'est prévue.

## Documentation de référence pour la fiabilité

### API exacte du jeu livré

La première source technique est `doc-html/runtime-api.json` dans l'installation personnelle de Factorio 2.0.77, exclue du dépôt. Ses métadonnées doivent confirmer la version. Les [pages officielles 2.0.77](https://lua-api.factorio.com/2.0.77/) permettent la navigation publique ; les pages `latest` peuvent concerner une autre version.

| Référence | Ce qu'elle impose au contrat |
|---|---|
| [LuaControl](https://lua-api.factorio.com/2.0.77/classes/LuaControl.html) | `begin_crafting` indique la quantité démarrée, pas terminée ; `can_insert` accepte qu'une partie seulement puisse entrer. La marche doit être vérifiée par position réelle |
| [LuaInventory](https://lua-api.factorio.com/2.0.77/classes/LuaInventory.html) | Inventaires identifiés par rôle, filtres et limites ; contenu 2.0 avec qualité. Mesurer les transferts réels plutôt que déduire un booléen de réussite |
| [LuaTransportLine](https://lua-api.factorio.com/2.0.77/classes/LuaTransportLine.html) | Les objets sur les tapis sont des stocks en transit distincts des coffres et machines |
| [LuaFluidBox](https://lua-api.factorio.com/2.0.77/classes/LuaFluidBox.html) | Dédupliquer les segments fluides, conserver quantités/températures/capacités et traiter les buffers sans segment |
| [LuaEntity](https://lua-api.factorio.com/2.0.77/classes/LuaEntity.html) et [LuaRecipe](https://lua-api.factorio.com/2.0.77/classes/LuaRecipe.html) | Recette réellement active, composants de machine, retours de changement de recette et capacités effectives |
| [Événements](https://lua-api.factorio.com/2.0.77/events.html) | Distinguer les événements précédant un effet de ceux le confirmant ; dommages et destructions ne sont pas un journal universel complet du monde |
| [LuaRCON](https://lua-api.factorio.com/2.0.77/classes/LuaRCON.html) | Le résultat immédiat répond à l'appel courant ; une opération longue exige un état consultable et une confirmation différée |
| [Cycle de vie](https://lua-api.factorio.com/2.0.77/auxiliary/data-lifecycle.html) | Persistance et réenregistrement des handlers ; ne pas modifier l'état sauvegardé pendant `on_load` |
| [LuaForce](https://lua-api.factorio.com/2.0.77/classes/LuaForce.html) | `is_chunk_charted` décrit l'exploration ; `is_chunk_visible` décrit la visibilité actuelle. Filtrer les ennemis avant export. La visibilité est fournie par chunk et par force, pas par sprite ou par observateur individuel |

Constat de faisabilité à qualifier au jalon d'acteur : `LuaEntity` hérite de `LuaControl`, ce qui fournit une piste pour un personnage indépendant. En revanche, `LuaPlayer.create_character()` exige un joueur connecté et `LuaPlayer.character` renvoie `nil` pour un joueur déconnecté. Un simple joueur virtuel ne résout donc pas à lui seul l'exécution headless ni le rattachement au même avatar. [LuaPlayer](https://lua-api.factorio.com/2.0.77/classes/LuaPlayer.html).

Les articles des développeurs [sur les invariants](https://www.factorio.com/blog/post/fff-242) et [sur la simulation multijoueur](https://www.factorio.com/blog/post/fff-302) justifient la vérification du même mod lors d'une connexion réelle : invariants cassés, état non persisté et logique de chargement peuvent produire des divergences difficiles à diagnostiquer. Ces articles historiques expliquent des principes ; ils ne remplacent pas l'API 2.0.77.

### Ollama et validation C#

[Ollama Structured Outputs](https://docs.ollama.com/capabilities/structured-outputs) précise que les sorties structurées contraintes ne sont actuellement pas prises en charge par Ollama Cloud. Cette limitation concerne donc notre modèle `glm-5.3-flash:cloud` et remplace la proposition initiale de génération contrainte par schéma. Le [function calling](https://docs.ollama.com/capabilities/tool-calling) transporte des arguments proposés par le modèle ; leur schéma descriptif n'en garantit ni la conformité ni la validité dans le jeu.

Proposition : objectifs stratégiques libres, références sémantiques validées et traduction en critères mesurables avant matérialisation des commandes par le C#. Le JSON du transport est produit par le sérialiseur. La documentation Microsoft décrit le [refus des champs inconnus](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/missing-members) et les [propriétés/paramètres requis](https://learn.microsoft.com/en-us/dotnet/standard/serialization/system-text-json/required-properties). Ces réglages doivent être complétés par des règles métier, des limites, le contrôle de fraîcheur et des tests de réponses tronquées ou contradictoires.

La [fiche GLM-5.3-Flash](https://ollama.com/library/glm-5.3-flash:cloud) documente les capacités d'appels d'outils et un raisonnement toujours actif, avec efforts `low`, `high`, `max`. Le profil initial prévoit `low`, configurable, sans `think: false`. L'appel passe par `POST http://localhost:11434/api/chat` et le service local transmet l'inférence au cloud selon la [documentation Ollama Cloud](https://docs.ollama.com/cloud). Le modèle exact a été observé dans `/api/tags` avec son hôte distant ; aucune inférence n'a été exécutée pendant cette mise à jour documentaire.

Le contexte envoyé est borné par l'application. La [documentation de contexte](https://docs.ollama.com/context-length) indique que les modèles cloud utilisent leur contexte maximal par défaut : un paramètre local `num_ctx` ne doit pas être présenté comme une garantie de limite distante. La [documentation des erreurs](https://docs.ollama.com/api/errors) sert à distinguer requête invalide, modèle absent, limitation et panne. Les reprises d'inférence sont bornées ; elles ne déclenchent jamais une répétition aveugle d'une mutation Factorio.

### Publication GitHub

La publication autorisée cible `skyline624/factorio_llm`, public, après préparation et revue du contenu réellement indexé. Le code original est sous MIT. La [documentation GitHub CLI](https://cli.github.com/manual/gh_repo_create) décrit la création ; l'existence distante, la visibilité, le commit et le résultat CI doivent être vérifiés séparément avant d'être déclarés acquis.

## Décisions de conception proposées à partir des sources

1. Prioriser l'état observable, l'identité des opérations et les critères de résultat avant la stratégie LLM.
2. Démontrer le personnage headless et ses actions natives avant de figer le reste du contrôleur.
3. Calculer en C# besoins, positions, routes et raccordements ; réserver au LLM les intentions et priorités.
4. Maintenir un catalogue de compétences avec préconditions, effets et preuves de réussite, puis une mémoire des résultats et échecs.
5. Séparer la réaction aux attaques des inférences Ollama ; arbitrer toutes les commandes du personnage.
6. Associer à toute mesure son périmètre, sa date et sa complétude ; réconcilier périodiquement les événements avec l'état réel.
7. Déclarer un résultat inconnu après une coupure ambiguë, puis consulter/réconcilier avant toute répétition.
8. Évaluer une production soutenue et une campagne avec ennemis ; une installation posée, un débit tamponné ou une démonstration avec téléportation ne valident pas l'objectif.

Les décisions actives se trouvent dans le [plan d'exécution](execution-plan.md), la [conception](design.md), la [feuille de route](implementation-plan.md) et le [complément cloud/public](superpowers/plans/2026-09-12-ollama-cloud-github-public.md). La qualification finale demande trois campagnes documentées ; l'analyse de sources n'en remplace aucune.
