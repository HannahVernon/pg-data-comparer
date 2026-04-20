# Contributing to pg-data-comparer

Thank you for your interest in contributing to pg-data-comparer!

## Code of Conduct

This project follows the [Contributor Covenant Code of Conduct](CODE_OF_CONDUCT.md). By participating, you agree to uphold this code.

## Getting Started

### Prerequisites

- [.NET 9 SDK](https://dotnet.microsoft.com/download/dotnet/9.0)
- Two PostgreSQL instances (or databases) for comparison testing

### Building

1. Clone the repository and switch to the `dev` branch:

   ```
   git clone https://github.com/HannahVernon/pg-data-comparer.git
   cd pg-data-comparer
   git switch dev
   ```

2. Build the project:

   ```
   dotnet build
   ```

## How to Contribute

### Reporting Bugs

Open a [bug report](https://github.com/HannahVernon/pg-data-comparer/issues/new?template=bug_report.yml) with steps to reproduce, expected vs. actual behavior, and your environment details.

### Suggesting Features

Open a [feature request](https://github.com/HannahVernon/pg-data-comparer/issues/new?template=feature_request.yml) describing the problem and your proposed solution.

### Pull Requests

1. Fork the repository and create a branch from `dev`:

   ```
   git switch -c feature/your-feature dev
   ```

2. Make your changes, keeping commits focused.

3. Ensure the project builds with zero warnings:

   ```
   dotnet build
   ```

4. Push your branch and open a pull request targeting `dev`.

## Branching Model

- `dev` is the integration branch for ongoing work.
- `main` contains stable releases.
- Create `feature/` or `fix/` branches from `dev`.

## License

This project is licensed under the [MIT License](LICENSE.md). By contributing, you agree that your contributions will be licensed under the same terms.