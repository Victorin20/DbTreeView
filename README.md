# DB Tree View

DB Tree View est un MVP Blazor qui se connecte à une base SQL Server et affiche
les dépendances entre procédures stockées, vues, fonctions et tables sous forme
de graphe interactif.

## Fonctionnalités disponibles

- Authentification Windows ou SQL Server.
- Test de la connexion avant l'analyse.
- Lecture en mémoire des objets utilisateur de la base.
- Dépendances issues de `sys.sql_expression_dependencies`.
- Relations de clés étrangères issues de `sys.foreign_keys`.
- Graphe Cytoscape.js avec zoom, recherche et plusieurs dispositions.
- Inspection des dépendances entrantes et sortantes d'un objet.
- Références externes ou non résolues affichées en gris.
- Mode démonstration ne nécessitant pas SQL Server.

Les identifiants ne sont enregistrés ni dans `appsettings.json`, ni dans un
fichier. Ils restent dans la session Blazor côté serveur et sont oubliés à la
fermeture de la session ou avec le bouton **Changer de base**.

## Prérequis

- Visual Studio avec la charge de travail **Développement ASP.NET et web**, ou
  le SDK .NET 10.
- Une instance SQL Server accessible depuis la machine qui exécute
  l'application.
- Un compte SQL disposant au minimum d'un accès à la base et de la permission
  de consulter ses métadonnées.

## Lancer le projet

Depuis Visual Studio, ouvrir `DbTreeView.slnx`, sélectionner `DbTreeView.Web`
comme projet de démarrage puis lancer le profil HTTPS.

En ligne de commande :

```powershell
dotnet restore DbTreeView.slnx
dotnet run --project DbTreeView.Web
```

Le formulaire propose `(localdb)\MSSQLLocalDB` par défaut. Remplacer ce nom par
le serveur réel, par exemple `SQL-PROD-01`, `localhost` ou
`SERVEUR\INSTANCE`.

Avec l'authentification Windows, l'identité utilisée est celle du processus qui
exécute l'application. En développement depuis Visual Studio, il s'agit en
général de l'utilisateur Windows courant. Après un déploiement IIS, il faudra
configurer explicitement l'identité du pool d'applications ou utiliser un compte
SQL dédié en lecture seule.

## Permissions SQL Server

La cartographie ne lit aucune donnée métier et n'exécute aucune procédure. Elle
interroge uniquement le catalogue système de la base.

La permission suivante permet normalement de rendre les définitions visibles :

```sql
USE [MaBase];
GRANT VIEW DEFINITION TO [MonUtilisateur];
```

Le compte doit également pouvoir se connecter à la base. Selon la politique de
permissions du serveur, l'administrateur peut devoir accorder l'accès en lecture
à `sys.sql_expression_dependencies`.

Ne pas donner `db_owner` à l'application uniquement pour contourner un problème
de permissions. Un compte dédié possédant les droits minimaux est préférable.

## Limites connues du MVP

- Une seule base est analysée à la fois.
- Les appels construits avec du SQL dynamique ne sont pas toujours détectables.
- Une référence vers une autre base ou un autre serveur est affichée comme objet
  externe, mais la base distante n'est pas encore analysée.
- Les statistiques de performance et Query Store feront partie de l'étape
  suivante.

## Organisation

```text
DbTreeView.Web/
├── Components/       interface Blazor
├── Models/           objets et relations du graphe
├── Services/         session et lecture du catalogue SQL Server
└── wwwroot/          styles, Cytoscape.js et intégration du graphe
```

La requête principale se trouve dans
`Services/SqlServerMetadataService.cs`. Le rendu interactif se trouve dans
`wwwroot/graph.js`.
