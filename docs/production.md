# Première boucle de production

La boucle C# traduit un stock cible d'objets solides en extraction, fabrication manuelle et alimentation d'un four à combustible. Elle lit les recettes et produits natifs, recherche les ingrédients manquants, choisit les positions avec le planificateur spatial, puis vérifie les stocks réels après les actions. Elle ne construit pas encore une chaîne automatisée avec foreuses, bras et tapis.

## Données et décisions

`production_catalog` expose à un tick unique les recettes non cachées avec leur disponibilité, ingrédients et produits, les catégories de fabrication du personnage, les produits d'extraction des ressources et arbres, les combustibles et les fours compatibles. C# accepte actuellement les transformations solides déterministes. Un cycle, une recette verrouillée, un fluide ou un résultat probabiliste ne devient pas une production promise.

Le planificateur choisit une prochaine action et relit l'inventaire après son exécution. Les objectifs portent actuellement sur un seuil dans **l'inventaire principal du personnage**, entre 1 et 1 000 objets. La cuisson est découpée en lots de 16 fabrications au plus. Les stocks des autres inventaires ne sont pas présentés comme des objets déjà transportés par le personnage ; les sorties de four disponibles sont récupérées avant d'engager une nouvelle extraction.

Les ressources sont cherchées dans la photographie locale, de rayon 48 cases. Quand une ressource manque, l'exploration choisit des frontières à partir du terrain observé et mémorise les ressources vues pendant cet appel. Les trajets utilisent uniquement la géométrie locale courante, y compris pour revenir à un four connu. Cette mémoire n'est pas encore persistée entre processus.

Les positions et orientations sont calculées par C#. La marche à huit directions du moteur a nécessité des segments d'au plus 0,75 case sur les routes obliques. Les segments restants sont conservés lorsqu'une nouvelle observation les confirme libres ; un blocage entraîne une replanification. La portée réduite des ressources est traitée séparément de l'approche des arbres.

Un four consomme un véritable objet de construction. Ses ingrédients et son combustible sont transférés depuis le personnage. La production engagée, les ingrédients encore stockés et les produits terminés sont comptés dans la même photographie d'usine ; un ingrédient déjà consommé par une cuisson n'est pas demandé une seconde fois. La réussite est établie par les stocks relus, jamais par un délai estimé.

## Objectif fourni par le modèle

`run-goal` sollicite une fois `glm-5.3-flash:cloud` via Ollama local. Le contexte contient les stocks datés du personnage, les types de ressources et bâtiments observés, les recettes disponibles et les limites actuelles du contrôleur. Les identifiants privés de session, les noms de joueurs et les coordonnées ne sont pas transmis.

Le modèle reste libre de proposer un objectif. La traduction actuelle accepte seulement une production d'objets avec un identifiant natif exact et une quantité entière dans les limites indiquées. Un autre objectif est journalisé comme non pris en charge ; aucune reformulation approximative ni aucun code du modèle n'est exécuté.

La défense C# est consultée pendant l'attente du modèle et pendant les opérations de production. La commande conserve son objectif pendant l'exécution sans rappeler le modèle. Les intentions, propositions, décisions, photographies utilisées et reçus sont consignés sous `.runtime/`.

`run-goal` et `produce` partagent la sélection de méthode : vérifier le stock actuel, rechercher une connexion foreuse–four compatible, puis choisir l'extraction automatique ou la production par le personnage. La sélection est une lecture sans mutation ; le contrôleur retenu relit l'état avant d'agir. Un défaut de collecte est propagé, sans être interprété comme une absence de capacité. Après le choix, aucune exception ne déclenche une méthode de repli : les effets partiels ou inconnus doivent être réconciliés. Les dépendances internes d'une fabrication utilisent encore le contrôleur de production par le personnage ; la sélection automatique concerne actuellement le produit cible de l'objectif.

## Commandes

```powershell
$hostDll = 'src/Factorio.Agent.Host/bin/Release/net10.0/Factorio.Agent.Host.dll'
$sessionFile = '.runtime/campaign-.../session.json'
dotnet $hostDll produce --session $sessionFile --item iron-plate --quantity 20
dotnet $hostDll run-goal --session $sessionFile
```

`produce` exécute un objectif explicite C#. `run-goal` effectue un appel cloud puis tente l'objectif proposé. Ces essais utilisent les règles ordinaires et ne créent pas de ressources. Ils restent des essais de développement, distincts des trois campagnes de qualification depuis le départ jusqu'à la fusée.

## Preuves et limites

### Extraction directe vers un four

`automate-smelting --session FILE --item iron-plate --quantity 100` vise le stock total du personnage. Le C# lit le rayon de minage, les catégories de ressources, les masques, les dimensions et le vecteur de sortie natifs. Il énumère les positions alignées sur les tuiles et les quatre orientations vers les fours compatibles observés. Un gisement mixte susceptible de contaminer la recette est refusé. Aucune position ni aucun gabarit n'est fourni par le LLM.

La validation utilise la case de dépose : la boîte du receveur doit intersecter cette case. Les vecteurs de prototypes sont des tableaux flottants, tandis que les positions effectives du moteur sont quantifiées. Après construction, la position réelle de sortie et la cible native, lorsqu'elle est disponible, sont contrôlées. Cette cible peut rester absente tant que la foreuse manque de combustible ; seule l'observation des produits établit ensuite la réussite de production.

Une foreuse déjà posée est réutilisée après vérification de sa connexion et des ressources encore observées. Le personnage récolte du combustible si nécessaire, ravitaille les machines et reprend les plaques. La foreuse extrait et dépose le minerai sans opération de minage du fer par le personnage. Ce mécanisme ne pose pas encore les fours prérequis, les tapis ou les bras ; il ne relocalise pas les machines lors de l'épuisement d'un gisement. Le choix du combustible et les trajets de ravitaillement restent à optimiser.

Le premier essai normal a atteint 85 plaques à partir de 80 entre les ticks 184 060 et 187 335. Le journal contient uniquement du minage de bois pour le combustible. Une lecture native ultérieure confirme quatre plaques supplémentaires en sortie du four, neuf nouvelles plaques produites au total et la cible de dépose correspondant au four. Le calcul initial de connexion avait été trop strict ; la foreuse construite a été conservée puis réutilisée après correction, sans resoumission de la construction. Cet essai de développement ne constitue pas une campagne sans correction.

Avec le pilote ensuite connecté au même avatar, un deuxième objectif a porté le stock de 85 à 100 plaques entre les ticks 192 779 et 199 630. Seuls deux arbres ont été minés par le personnage pour le combustible. La lecture native finale retrouve un seul avatar, la même foreuse déposant dans le même four, 100 plaques transportées et six supplémentaires en sortie. Le four compte alors 118 produits depuis sa construction, contre 92 avant les essais d'extraction automatique. Le mode pacifique reste désactivé. Cet essai précédait l'intégration de la sélection de méthode.

Une fixture distincte, avec 99 entités de minerai et du bois ajoutés explicitement pour le test, a ensuite vérifié l'installation neuve avec le code corrigé : construction de la foreuse, alimentation en combustible et stock de huit à treize plaques, ticks 29 786 à 31 344. Aucun correctif ni nouvelle soumission manuelle n'a été nécessaire pendant cette exécution. Cette préparation artificielle exclut la fixture des campagnes normales.

### Production initiale et LLM

Dans la partie normale de développement sur la graine 424242, le contrôleur a trouvé le fer, extrait 12 minerais, construit un four et atteint 20 plaques à partir des huit plaques initiales. Une lecture indépendante du moteur confirme un four, 12 produits terminés, aucun minerai ni plaque restant dans ses inventaires d'entrée/sortie et 20 plaques dans le personnage. Le client est connecté au même personnage. Pollution, évolution et expansion restent actives, mode pacifique désactivé.

Plusieurs corrections et relances du contrôleur ont été nécessaires dans ce même monde : tolérance des points de passage, oscillation des coins, guidage oblique, portée de minage et cuisson déjà engagée. Aucun retour à une ancienne sauvegarde ni apport artificiel n'a été utilisé pour ces résultats. Cet essai n'est donc pas présenté comme une campagne autonome sans interruption.

Ensuite, `glm-5.3-flash:cloud` a proposé un stock de **100 plaques de fer** à partir des faits du jeu. L'appel unique a pris environ 1,755 seconde, avec 1 158 tokens d'entrée et 139 de sortie. Le C# a exécuté cet objectif de 20 à 100 plaques entre les ticks 103 167 et 132 815, soit environ 8 minutes 14 secondes de jeu, sans intervention pendant l'objectif.

La lecture native finale confirme 100 plaques transportées, 92 produits terminés par l'unique four depuis sa construction, aucune cuisson encore engagée et aucun minerai ou produit restant dans ses inventaires d'entrée/sortie. Le même personnage est attaché au pilote. `steam-power` est recherché par le déclencheur natif de production ; aucune recherche n'a été forcée. Le mod rapporte zéro intervention manuelle, aucun marquage fixture et zéro fusée lancée. Ce premier objectif réel du LLM ne remplace pas une campagne complète depuis le départ.

Après intégration de la sélection, un nouvel appel réel a librement proposé deux `steam-engine`, en 2,290 secondes et une tentative (1 257 tokens d'entrée, 158 de sortie). Le chemin par le personnage a atteint deux objets à partir de zéro, ticks 218 218 à 218 888. Un objectif C# séparé de 65 plaques a ensuite choisi l'extraction automatique, réutilisé la foreuse 613 et le four 222, puis porté le stock de 58 à 65, ticks 221 338 à 221 821. Il ne s'agit pas d'une proposition de plaques par le LLM. La lecture indépendante du moteur au tick 224 739 confirme un seul personnage, les deux machines à vapeur transportées, 65 plaques transportées, six en sortie et 125 produits terminés par le four. Le mode pacifique est désactivé. Le checkpoint suivant a été sauvegardé et le serveur arrêté proprement. Ces nouveaux essais ont été réalisés en headless ; aucun nouveau client graphique n'a été lancé.

Les budgets actuels sont de 15 minutes et 256 étapes pour le contrôleur par le personnage, ou 600 itérations pour l'extraction automatique, avec des bornes supplémentaires pour déplacement et cuisson. La construction automatique des machines prérequises, les fluides, les réseaux de production, la récupération après mort, la reprise persistante et le traitement de tous les objectifs libres restent incomplets. Un changement de contrôle ou d'incarnation arrête la production avec demande de réconciliation. Les trois campagnes jusqu'à la fusée restent à démontrer.
